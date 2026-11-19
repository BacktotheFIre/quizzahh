using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Xml;

// Loads Heather's rigged COLLADA model, poses her skeleton and skins the mesh on the CPU.
// Matrices are 4x4 row-major with column vectors (the COLLADA convention): p' = M * p.
class HeatherModel
{
    public class Joint
    {
        public string Name;
        public int Parent = -1;
        public double[] Local;
        public double[] World;
        public double Rx, Ry, Rz; // extra pose rotation in degrees, applied in the joint's local space
    }

    class SkinMesh
    {
        public double[] BindPos, BindNrm; // xyz triples, bind-shape already applied
        public int[][] Joints;            // per vertex: indices into the controller's joint list
        public double[][] Weights;
        public double[][] InvBind;        // per controller joint
        public int[] JointMap;            // controller joint -> skeleton joint
        public MeshGeometry3D Geo;
        public string Texture;
    }

    public readonly List<Joint> Joints = new List<Joint>();
    readonly Dictionary<string, int> byName = new Dictionary<string, int>();
    readonly List<SkinMesh> meshes = new List<SkinMesh>();
    readonly double[] rootMatrix;

    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    const string NS = "http://www.collada.org/2005/11/COLLADASchema";

    public Joint this[string name] { get { int i; return byName.TryGetValue(name, out i) ? Joints[i] : null; } }

    public HeatherModel(Stream dae, Func<string, BitmapSource> loadTexture)
    {
        var doc = new XmlDocument();
        doc.Load(dae);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("c", NS);

        // Skeleton
        rootMatrix = Identity();
        var armature = doc.SelectSingleNode("//c:visual_scene/c:node[c:node[@type='JOINT']]", ns);
        if (armature != null)
        {
            var m = armature.SelectSingleNode("c:matrix", ns);
            if (m != null) rootMatrix = Floats(m.InnerText);
            foreach (XmlNode child in armature.SelectNodes("c:node[@type='JOINT']", ns)) AddJoint(child, -1, ns);
        }

        // Image ids -> file names, and material ids -> texture file
        var images = new Dictionary<string, string>();
        foreach (XmlNode img in doc.SelectNodes("//c:library_images/c:image", ns))
        {
            var f = img.SelectSingleNode(".//c:init_from", ns);
            if (f != null) images[img.Attributes["id"].Value] = System.IO.Path.GetFileName(Uri.UnescapeDataString(f.InnerText.Trim()));
        }
        var effectTex = new Dictionary<string, string>();
        foreach (XmlNode eff in doc.SelectNodes("//c:library_effects/c:effect", ns))
        {
            foreach (XmlNode init in eff.SelectNodes(".//c:init_from", ns))
            {
                string t = init.InnerText.Trim();
                if (images.ContainsKey(t)) { effectTex[eff.Attributes["id"].Value] = images[t]; break; }
                if (t.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { effectTex[eff.Attributes["id"].Value] = t; break; }
            }
        }
        var matTex = new Dictionary<string, string>();
        foreach (XmlNode mat in doc.SelectNodes("//c:library_materials/c:material", ns))
        {
            var ie = mat.SelectSingleNode("c:instance_effect", ns);
            string tex;
            if (ie != null && effectTex.TryGetValue(ie.Attributes["url"].Value.TrimStart('#'), out tex))
                matTex[mat.Attributes["id"].Value] = tex;
        }

        // Skinned meshes
        var textures = new Dictionary<string, BitmapSource>();
        foreach (XmlNode inst in doc.SelectNodes("//c:visual_scene//c:instance_controller", ns))
        {
            var ctrl = doc.SelectSingleNode("//c:controller[@id='" + inst.Attributes["url"].Value.TrimStart('#') + "']/c:skin", ns);
            if (ctrl == null) continue;
            var symbolTex = new Dictionary<string, string>();
            foreach (XmlNode im in inst.SelectNodes(".//c:instance_material", ns))
            {
                string tex;
                if (matTex.TryGetValue(im.Attributes["target"].Value.TrimStart('#'), out tex)) symbolTex[im.Attributes["symbol"].Value] = tex;
            }
            var geom = doc.SelectSingleNode("//c:geometry[@id='" + ctrl.Attributes["source"].Value.TrimStart('#') + "']/c:mesh", ns);
            if (geom == null) continue;
            LoadSkin(ctrl, geom, symbolTex, ns);
        }
        foreach (var sm in meshes)
        {
            if (sm.Texture != null && !textures.ContainsKey(sm.Texture))
                textures[sm.Texture] = loadTexture(sm.Texture);
        }
        textureCache = textures;
    }

    readonly Dictionary<string, BitmapSource> textureCache;

    void AddJoint(XmlNode node, int parent, XmlNamespaceManager ns)
    {
        var j = new Joint { Parent = parent };
        var sid = node.Attributes["sid"];
        j.Name = sid != null ? sid.Value : node.Attributes["name"].Value;
        var m = node.SelectSingleNode("c:matrix", ns);
        j.Local = m != null ? Floats(m.InnerText) : Identity();
        int idx = Joints.Count;
        Joints.Add(j);
        byName[j.Name] = idx;
        if (node.Attributes["name"] != null) byName[node.Attributes["name"].Value] = idx;
        foreach (XmlNode child in node.SelectNodes("c:node[@type='JOINT']", ns)) AddJoint(child, idx, ns);
    }

    void LoadSkin(XmlNode skin, XmlNode mesh, Dictionary<string, string> symbolTex, XmlNamespaceManager ns)
    {
        Func<string, double[]> source = id => Floats(mesh.SelectSingleNode("c:source[@id='" + id.TrimStart('#') + "']/c:float_array", ns).InnerText);

        // Skin data
        var bindShape = Identity();
        var bs = skin.SelectSingleNode("c:bind_shape_matrix", ns);
        if (bs != null) bindShape = Floats(bs.InnerText);
        var jointsInput = skin.SelectSingleNode("c:joints/c:input[@semantic='JOINT']", ns).Attributes["source"].Value.TrimStart('#');
        var invInput = skin.SelectSingleNode("c:joints/c:input[@semantic='INV_BIND_MATRIX']", ns).Attributes["source"].Value.TrimStart('#');
        var names = skin.SelectSingleNode("c:source[@id='" + jointsInput + "']/c:Name_array", ns).InnerText
            .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var inv = Floats(skin.SelectSingleNode("c:source[@id='" + invInput + "']/c:float_array", ns).InnerText);
        var vw = skin.SelectSingleNode("c:vertex_weights", ns);
        var wSrc = vw.SelectSingleNode("c:input[@semantic='WEIGHT']", ns).Attributes["source"].Value.TrimStart('#');
        var weightsArr = Floats(skin.SelectSingleNode("c:source[@id='" + wSrc + "']/c:float_array", ns).InnerText);
        int jOff = int.Parse(vw.SelectSingleNode("c:input[@semantic='JOINT']", ns).Attributes["offset"].Value);
        int wOff = int.Parse(vw.SelectSingleNode("c:input[@semantic='WEIGHT']", ns).Attributes["offset"].Value);
        int stride = vw.SelectNodes("c:input", ns).Count;
        var vcount = Ints(vw.SelectSingleNode("c:vcount", ns).InnerText);
        var v = Ints(vw.SelectSingleNode("c:v", ns).InnerText);

        var posJoints = new int[vcount.Length][];
        var posWeights = new double[vcount.Length][];
        int cursor = 0;
        for (int i = 0; i < vcount.Length; i++)
        {
            posJoints[i] = new int[vcount[i]];
            posWeights[i] = new double[vcount[i]];
            double sum = 0;
            for (int k = 0; k < vcount[i]; k++)
            {
                posJoints[i][k] = v[cursor + jOff];
                posWeights[i][k] = weightsArr[v[cursor + wOff]];
                sum += posWeights[i][k];
                cursor += stride;
            }
            if (sum > 0) for (int k = 0; k < vcount[i]; k++) posWeights[i][k] /= sum;
        }

        // Geometry
        var vertsInput = mesh.SelectSingleNode("c:vertices/c:input[@semantic='POSITION']", ns).Attributes["source"].Value;
        var positions = source(vertsInput);

        foreach (XmlNode prim in mesh.SelectNodes("c:triangles|c:polylist", ns))
        {
            int pOff = 0, nOff = -1, tOff = -1, pstride = 0;
            double[] normals = null, uvs = null;
            foreach (XmlNode input in prim.SelectNodes("c:input", ns))
            {
                int off = int.Parse(input.Attributes["offset"].Value);
                pstride = Math.Max(pstride, off + 1);
                switch (input.Attributes["semantic"].Value)
                {
                    case "VERTEX": pOff = off; break;
                    case "NORMAL": nOff = off; normals = source(input.Attributes["source"].Value); break;
                    case "TEXCOORD":
                        if (tOff < 0 && (input.Attributes["set"] == null || input.Attributes["set"].Value == "0"))
                        { tOff = off; uvs = source(input.Attributes["source"].Value); }
                        break;
                }
            }
            var p = Ints(prim.SelectSingleNode("c:p", ns).InnerText);
            int corners = p.Length / pstride;

            // Corner order: triangles are already triangles; polylist gets fan-triangulated
            var order = new List<int>();
            var vc = prim.SelectSingleNode("c:vcount", ns);
            if (vc != null)
            {
                int start = 0;
                foreach (int n in Ints(vc.InnerText))
                {
                    for (int k = 1; k + 1 < n; k++) { order.Add(start); order.Add(start + k); order.Add(start + k + 1); }
                    start += n;
                }
            }
            else for (int c = 0; c < corners; c++) order.Add(c);

            var sm = new SkinMesh();
            string tex;
            var matAttr = prim.Attributes["material"];
            sm.Texture = matAttr != null && symbolTex.TryGetValue(matAttr.Value, out tex) ? tex : null;
            var keyToIndex = new Dictionary<long, int>();
            var bp = new List<double>();
            var bn = new List<double>();
            var uvList = new PointCollection();
            var js = new List<int[]>();
            var ws = new List<double[]>();
            var tris = new Int32Collection();
            foreach (int c in order)
            {
                int pi = p[c * pstride + pOff];
                int ni = nOff >= 0 ? p[c * pstride + nOff] : 0;
                int ti = tOff >= 0 ? p[c * pstride + tOff] : 0;
                long key = ((long)pi << 40) + ((long)ni << 20) + ti;
                int index;
                if (!keyToIndex.TryGetValue(key, out index))
                {
                    index = bp.Count / 3;
                    keyToIndex[key] = index;
                    var pt = Transform(bindShape, positions[pi * 3], positions[pi * 3 + 1], positions[pi * 3 + 2], 1);
                    bp.AddRange(pt);
                    if (normals != null) bn.AddRange(Transform(bindShape, normals[ni * 3], normals[ni * 3 + 1], normals[ni * 3 + 2], 0));
                    else bn.AddRange(new double[] { 0, 0, 1 });
                    uvList.Add(uvs != null ? new Point(uvs[ti * 2], 1 - uvs[ti * 2 + 1]) : new Point(0, 0));
                    js.Add(posJoints[pi]);
                    ws.Add(posWeights[pi]);
                }
                tris.Add(index);
            }
            sm.BindPos = bp.ToArray();
            sm.BindNrm = bn.ToArray();
            sm.Joints = js.ToArray();
            sm.Weights = ws.ToArray();
            sm.InvBind = new double[names.Length][];
            sm.JointMap = new int[names.Length];
            for (int k = 0; k < names.Length; k++)
            {
                sm.InvBind[k] = inv.Skip(k * 16).Take(16).ToArray();
                int ji;
                sm.JointMap[k] = byName.TryGetValue(names[k], out ji) ? ji : -1;
            }
            sm.Geo = new MeshGeometry3D { TriangleIndices = tris, TextureCoordinates = uvList };
            meshes.Add(sm);
        }
    }

    // Meshes left out of the model (e.g. alternate hand shapes the game swaps between)
    public readonly HashSet<int> Hidden = new HashSet<int>();

    // Every 4th point of her posed body, with the name of the joint that moves it most (for finding
    // where she passes through herself; tests only)
    public IEnumerable<KeyValuePair<string, Point3D>> SomePointsByJoint()
    {
        for (int m = 0; m < meshes.Count; m++)
        {
            var sm = meshes[m];
            if (Hidden.Contains(m) || sm.Geo == null) continue;
            var pts = sm.Geo.Positions;
            for (int i = 0; i < pts.Count; i += 4)
            {
                int best = -1; double bw = 0;
                for (int k = 0; k < sm.Joints[i].Length; k++) if (sm.Weights[i][k] > bw) { bw = sm.Weights[i][k]; best = sm.Joints[i][k]; }
                int sj = best >= 0 ? sm.JointMap[best] : -1;
                yield return new KeyValuePair<string, Point3D>(sj >= 0 ? Joints[sj].Name : "", pts[i]);
            }
        }
    }

    // Is this joint the given one, or further down the chain from it (a hand is under its upper arm)?
    public bool Under(string joint, string top)
    {
        int i;
        if (!byName.TryGetValue(joint, out i)) return false;
        for (; i >= 0; i = Joints[i].Parent) if (Joints[i].Name == top) return true;
        return false;
    }

    // Every 8th point of her posed body (for measuring how far she reaches; tests only)
    public IEnumerable<Point3D> SomePoints()
    {
        for (int m = 0; m < meshes.Count; m++)
        {
            if (Hidden.Contains(m) || meshes[m].Geo == null) continue;
            var pts = meshes[m].Geo.Positions;
            for (int i = 0; i < pts.Count; i += 8) yield return pts[i];
        }
    }

    // One line per mesh: index, vertex count, texture, and the joints that move it most
    public List<string> DebugMeshes()
    {
        var lines = new List<string>();
        for (int m = 0; m < meshes.Count; m++)
        {
            var sm = meshes[m];
            var weight = new Dictionary<string, double>();
            for (int i = 0; i < sm.Joints.Length; i++)
                for (int k = 0; k < sm.Joints[i].Length; k++)
                {
                    int ji = sm.JointMap[sm.Joints[i][k]];
                    string name = ji >= 0 ? Joints[ji].Name : "?";
                    double w;
                    weight.TryGetValue(name, out w);
                    weight[name] = w + sm.Weights[i][k];
                }
            var top = weight.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key + ":" + (int)kv.Value);
            lines.Add(m + " verts=" + sm.BindPos.Length / 3 + " tex=" + sm.Texture + " " + string.Join(" ", top));
        }
        return lines;
    }

    public Model3DGroup BuildModel()
    {
        var group = new Model3DGroup();
        for (int mi = 0; mi < meshes.Count; mi++)
        {
            var sm = meshes[mi];
            if (Hidden.Contains(mi)) continue;
            Material mat;
            BitmapSource tex;
            if (sm.Texture != null && textureCache.TryGetValue(sm.Texture, out tex) && tex != null)
            {
                var brush = new ImageBrush(tex) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 1) };
                brush.Freeze();
                mat = new DiffuseMaterial(brush);
            }
            else mat = new DiffuseMaterial(Brushes.Gray);
            group.Children.Add(new GeometryModel3D(sm.Geo, mat) { BackMaterial = mat });
        }
        Update();
        return group;
    }

    // Recompute only the joint transforms (no skinning): enough to read joint positions
    public void UpdateJoints()
    {
        for (int i = 0; i < Joints.Count; i++)
        {
            var j = Joints[i];
            var local = Mul(j.Local, Euler(j.Rx, j.Ry, j.Rz));
            j.World = Mul(j.Parent >= 0 ? Joints[j.Parent].World : rootMatrix, local);
        }
    }

    int updates;

    // Recompute joint transforms from the current pose and re-skin every mesh
    public void Update()
    {
        for (int i = 0; i < Joints.Count; i++)
        {
            var j = Joints[i];
            var local = Mul(j.Local, Euler(j.Rx, j.Ry, j.Rz));
            j.World = Mul(j.Parent >= 0 ? Joints[j.Parent].World : rootMatrix, local);
        }
        for (int mi = 0; mi < meshes.Count; mi++)
        {
            if (Hidden.Contains(mi)) continue; // never drawn, so don't spend time posing them
            var sm = meshes[mi];
            var skinMats = new double[sm.JointMap.Length][];
            for (int k = 0; k < skinMats.Length; k++)
                skinMats[k] = sm.JointMap[k] >= 0 ? Mul(Joints[sm.JointMap[k]].World, sm.InvBind[k]) : Identity();
            int n = sm.BindPos.Length / 3;
            // Reuse the same two lists every frame: take them off the mesh, overwrite them, put them
            // back. Making new ones each frame threw away over a megabyte a frame, and the clean-up
            // of all that (garbage collection) shows up as little hitches in her movement.
            var pts = sm.Geo.Positions;
            var nrms = sm.Geo.Normals;
            bool fresh = pts == null || nrms == null || pts.Count != n || nrms.Count != n || pts.IsFrozen || nrms.IsFrozen;
            // Normals (which way each bit of surface faces, for the lighting) change slowly, so they
            // are only redone every 4th frame. That halves what has to be sent to the graphics card
            // on the other three, and the shading being a few hundredths of a second old can't be seen.
            bool shade = fresh || updates % 4 == 0;
            if (fresh) { pts = new Point3DCollection(n); nrms = new Vector3DCollection(n); }
            else { sm.Geo.Positions = null; if (shade) sm.Geo.Normals = null; }
            for (int i = 0; i < n; i++)
            {
                double x = sm.BindPos[i * 3], y = sm.BindPos[i * 3 + 1], z = sm.BindPos[i * 3 + 2];
                double nx = sm.BindNrm[i * 3], ny = sm.BindNrm[i * 3 + 1], nz = sm.BindNrm[i * 3 + 2];
                double ox = 0, oy = 0, oz = 0, onx = 0, ony = 0, onz = 0;
                var jj = sm.Joints[i];
                var ww = sm.Weights[i];
                for (int k = 0; k < jj.Length; k++)
                {
                    var m = skinMats[jj[k]];
                    double w = ww[k];
                    ox += w * (m[0] * x + m[1] * y + m[2] * z + m[3]);
                    oy += w * (m[4] * x + m[5] * y + m[6] * z + m[7]);
                    oz += w * (m[8] * x + m[9] * y + m[10] * z + m[11]);
                    onx += w * (m[0] * nx + m[1] * ny + m[2] * nz);
                    ony += w * (m[4] * nx + m[5] * ny + m[6] * nz);
                    onz += w * (m[8] * nx + m[9] * ny + m[10] * nz);
                }
                if (jj.Length == 0) { ox = x; oy = y; oz = z; onx = nx; ony = ny; onz = nz; }
                if (fresh) { pts.Add(new Point3D(ox, oy, oz)); nrms.Add(new Vector3D(onx, ony, onz)); }
                else { pts[i] = new Point3D(ox, oy, oz); if (shade) nrms[i] = new Vector3D(onx, ony, onz); }
            }
            sm.Geo.Positions = pts;
            if (shade) sm.Geo.Normals = nrms;
        }
        updates++;
    }

    // How many points get re-posed every frame (the visible meshes only)
    public int VertexCount { get { return meshes.Where((m, i) => !Hidden.Contains(i)).Sum(m => m.BindPos.Length / 3); } }

    public Rect3D Bounds()
    {
        var b = Rect3D.Empty;
        for (int mi = 0; mi < meshes.Count; mi++)
            if (!Hidden.Contains(mi) && meshes[mi].Geo.Positions != null) b.Union(meshes[mi].Geo.Bounds);
        return b;
    }

    public void ResetPose()
    {
        foreach (var j in Joints) j.Rx = j.Ry = j.Rz = 0;
    }

    // Lowest point of the posed (visible) mesh after pitching it forward by pitchDeg around
    // the X axis through (pivotY, pivotZ). Used to keep her feet/knees/belly on the taskbar.
    public double LowestZ(double pitchDeg, double pivotY, double pivotZ)
    {
        double a = pitchDeg * Math.PI / 180, s = Math.Sin(a), c = Math.Cos(a);
        double min = double.MaxValue;
        for (int m = 0; m < meshes.Count; m++)
        {
            if (Hidden.Contains(m)) continue;
            var pts = meshes[m].Geo.Positions;
            if (pts == null) continue;
            for (int i = 0; i < pts.Count; i += 2)
            {
                var p = pts[i];
                double z = pivotZ + (p.Y - pivotY) * s + (p.Z - pivotZ) * c;
                if (z < min) min = z;
            }
        }
        return min == double.MaxValue ? 0 : min;
    }

    // World-space position of a joint (after the last Update)
    public Point3D JointPos(string name)
    {
        var j = this[name];
        return j == null ? new Point3D() : new Point3D(j.World[3], j.World[7], j.World[11]);
    }

    // ---------- math ----------
    static double[] Identity() { return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }; }

    static double[] Mul(double[] a, double[] b)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * b[12 + j];
        return r;
    }

    static double[] Euler(double rx, double ry, double rz)
    {
        if (rx == 0 && ry == 0 && rz == 0) return Identity();
        double a = rx * Math.PI / 180, b = ry * Math.PI / 180, c = rz * Math.PI / 180;
        var X = new double[] { 1, 0, 0, 0, 0, Math.Cos(a), -Math.Sin(a), 0, 0, Math.Sin(a), Math.Cos(a), 0, 0, 0, 0, 1 };
        var Y = new double[] { Math.Cos(b), 0, Math.Sin(b), 0, 0, 1, 0, 0, -Math.Sin(b), 0, Math.Cos(b), 0, 0, 0, 0, 1 };
        var Z = new double[] { Math.Cos(c), -Math.Sin(c), 0, 0, Math.Sin(c), Math.Cos(c), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        return Mul(Mul(X, Y), Z);
    }

    static double[] Transform(double[] m, double x, double y, double z, double w)
    {
        return new[]
        {
            m[0] * x + m[1] * y + m[2] * z + m[3] * w,
            m[4] * x + m[5] * y + m[6] * z + m[7] * w,
            m[8] * x + m[9] * y + m[10] * z + m[11] * w
        };
    }

    static double[] Floats(string s)
    {
        return s.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.Parse(t, NumberStyles.Float, CI)).ToArray();
    }

    static int[] Ints(string s)
    {
        return s.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    }
}

// A live, animated 3D Heather. Standing (next to the quiz) or sitting (on the taskbar).
class HeatherView : Grid
{
    public readonly HeatherModel Model;
    public readonly Viewport3D Viewport;
    public double Talk;            // 0..1, how hard she's talking right now (drives head bobs)
    public bool Talking, Looking;  // explaining gestures / looking at you

    readonly bool lying;
    readonly TranslateTransform3D lift = new TranslateTransform3D();
    readonly AxisAngleRotation3D turn = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastFrame = -1, talkSmooth;
    double nodAt = -9, shakeAt = -9, hopAt = -9, cheerAt = -9;

    internal static readonly string[] Skirt = { "bone012", "bone013", "bone016", "bone017" };

    // ---------- Taskbar mode ----------
    // She changes pose through real in-between poses:
    //   lie <-> allfours (hands and knees) <-> kneel <-> stand <-> crouch <-> sit (on the edge)
    //   stand <-> walk
    // Each move is eased between the pose she's in and the next one, so getting up, kneeling,
    // turning and sitting down all look like movement instead of a cross-fade.
    public int WalkDir = 1;
    public double Fps = 60; // frame rate while settled (taskbar Heather goes higher while walking, climbing or changing pose)
    public int Frames;       // how many frames she has drawn so far
    public int Ticks;        // how many times WPF has offered her the chance to draw one
    readonly Dictionary<string, double[]> cur = new Dictionary<string, double[]>();
    readonly Dictionary<string, double[]> tgt = new Dictionary<string, double[]>();
    double seatZ, hipY, hipZ;
    static readonly string[] BuddyJoints =
    {
        "bone002", "bone004", "bone006", "bone009", "bone003", "bone005", "bone007", "bone008", "bone021", "bone027",
        "bone012", "bone013", "bone016", "bone017", "bone026", "bone032", "bone034", "bone036", "bone039", "bone041"
    };

    // Body pitch (0 upright, 90 face-down) turns around her pelvis; yaw (turn) is around the vertical
    readonly AxisAngleRotation3D pitchRot = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);

    // One full-body pose. Which way she faces (yaw) is handled separately, so she can turn smoothly
    // on her own: she turns before kneeling down or walking off, never while her feet are planted.
    class BPose
    {
        public readonly Dictionary<string, double[]> J = new Dictionary<string, double[]>();
        public double Pitch, SeatW, Sink;

        public BPose Clone()
        {
            var p = new BPose { Pitch = Pitch, SeatW = SeatW, Sink = Sink };
            foreach (var kv in J) p.J[kv.Key] = (double[])kv.Value.Clone();
            return p;
        }
    }

    // Which poses connect, and how long each move takes (seconds)
    static readonly string[][] Moves =
    {
        // Getting up off her stomach the way a person does: push back from her elbows onto hands and
        // knees, bring one foot forward between her hands, straighten up onto one knee, then stand up
        // off the front leg. Lying down is the same in reverse.
        new[] { "lie", "allfours", "1.2" }, new[] { "allfours", "footfwd", "0.8" },
        new[] { "footfwd", "halfkneel", "0.8" }, new[] { "halfkneel", "stand", "1.1" },
        new[] { "stand", "crouch", "0.75" }, new[] { "crouch", "sit", "0.75" }, new[] { "stand", "walk", "0.5" },
        new[] { "stand", "climb", "0.6" }
    };

    // Facing: 0 = toward you, 90 = toward the right of the screen. On the floor she's turned partway.
    const double FloorYaw = 64;
    double yawNow, yawMid;
    bool yawSnap = true;

    double YawTarget(double t)
    {
        string a = state, b = segTo;
        if (b != null)
        {
            if (IsFloor(a) || IsFloor(b)) return FloorYaw * side;
            if (a == "walk" || b == "walk") return 90 * WalkDir;
            if (a == "climb" || b == "climb") return 180 * WalkDir; // her back to you, facing what she's climbing
            return 0;
        }
        if (IsFloor(a)) return FloorYaw * side;
        if (a == "walk") return 90 * WalkDir;
        if (a == "climb") return 180 * WalkDir;
        if (a == "stand" && path.Count > 0)
        {
            string n = path.Peek();
            if (n == "walk") return 90 * WalkDir;
            if (n == "climb") return 180 * WalkDir;
            if (IsFloor(n)) return FloorYaw * NextSide;
        }
        return a == "stand" ? 6 * Math.Sin(t * 0.13) : 0;
    }

    string state = "stand", goal = "stand", segTo;
    readonly Queue<string> path = new Queue<string>();
    BPose shown, from;
    double segStart, segDur;

    // What she's heading for. Setting it plans the moves to get there.
    public string Activity
    {
        get { return goal; }
        set { SetGoal(value); }
    }

    // The pose she's settled in, or null while she's moving between poses
    public string Settled { get { return segTo == null && path.Count == 0 ? state : null; } }

    // Jump straight into a pose (used when she first appears)
    public void SnapTo(string pose)
    {
        state = goal = pose;
        segTo = null;
        path.Clear();
        shown = null;
        endedMoving = false;
        anchor = null;
        driftX = 0;
        side = NextSide;
        yawSnap = true;
    }

    void SetGoal(string g)
    {
        if (g == goal) return;
        goal = g;
        path.Clear();
        foreach (var step in Route(segTo ?? state, g)) path.Enqueue(step);
    }

    // Shortest chain of moves between two poses
    static List<string> Route(string a, string b)
    {
        var prev = new Dictionary<string, string> { { a, null } };
        var q = new Queue<string>();
        q.Enqueue(a);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            if (n == b) break;
            foreach (var m in Moves)
            {
                string next = m[0] == n ? m[1] : m[1] == n ? m[0] : null;
                if (next != null && !prev.ContainsKey(next)) { prev[next] = n; q.Enqueue(next); }
            }
        }
        var route = new List<string>();
        if (!prev.ContainsKey(b)) return route;
        for (string n = b; n != a; n = prev[n]) route.Insert(0, n);
        return route;
    }

    static double MoveTime(string a, string b)
    {
        foreach (var m in Moves)
            if ((m[0] == a && m[1] == b) || (m[0] == b && m[1] == a))
                return double.Parse(m[2], CultureInfo.InvariantCulture);
        return 0.8;
    }

    // Smooth start and stop
    static double Ease(double k) { return k * k * k * (k * (6 * k - 15) + 10); }

    static BPose Lerp(BPose a, BPose b, double k)
    {
        var p = new BPose
        {
            Pitch = a.Pitch + (b.Pitch - a.Pitch) * k,
            SeatW = a.SeatW + (b.SeatW - a.SeatW) * k, Sink = a.Sink + (b.Sink - a.Sink) * k
        };
        foreach (var kv in b.J)
        {
            double[] av;
            if (!a.J.TryGetValue(kv.Key, out av)) av = new double[3];
            p.J[kv.Key] = new[]
            {
                av[0] + (kv.Value[0] - av[0]) * k, av[1] + (kv.Value[1] - av[1]) * k, av[2] + (kv.Value[2] - av[2]) * k
            };
        }
        return p;
    }

    // Where the camera looks, so screen positions (her head, for the speech bubble) can be worked out
    Point3D camCenter;
    Vector3D camUp, camRight;
    double camWpd;
    Transform3DGroup bodyT;

    // focus: world point that appears focusY pixels from the top (null = her seat when sitting)
    public HeatherView(HeatherModel model, bool sitting, double width, double height, Point3D? focus,
        double worldPerDip, Vector3D viewDir, double focusY, bool lying = false)
    {
        Model = model;
        this.lying = lying;
        Width = width;
        Height = height;

        var body = model.BuildModel();
        var hip = model.JointPos("bone003");
        hipY = hip.Y;
        hipZ = hip.Z;
        seatZ = hipZ - 42;
        var tg = new Transform3DGroup();
        // Taskbar mode: she bends forward (kneeling, crawling, lying down) around her hips
        pitchT = new RotateTransform3D(pitchRot, new Point3D(0, hipY, hipZ));
        turnT = new RotateTransform3D(turn, new Point3D(0, 0, 0));
        if (lying) tg.Children.Add(pitchT);
        tg.Children.Add(turnT);
        // Taskbar mode: while she's being carried her whole body swings from about her head, like a pendulum
        if (lying) tg.Children.Add(swayT = new RotateTransform3D(swayRot, new Point3D(0, 0, 0)));
        tg.Children.Add(lift);
        body.Transform = tg;
        bodyT = tg;
        if (lying)
        {
            // Real leg lengths for the walking IK, then how fast a planted foot travels
            var hp = model.JointPos("bone003");
            var kn = model.JointPos("bone007");
            var an = model.JointPos("bone021");
            thighLen = (kn - hp).Length;
            shinLen = (an - kn).Length;
            CalibrateWalk();
        }
        Pose(0, 0);
        if (!lying) model.Update();

        Point3D f;
        if (focus.HasValue) f = focus.Value;
        else f = new Point3D(0, hip.Y, hip.Z - 42);
        viewDir.Normalize();
        swayRot.Axis = viewDir; // she swings in the plane of the screen
        var up = Vector3D.CrossProduct(Vector3D.CrossProduct(viewDir, new Vector3D(0, 0, 1)), viewDir);
        up.Normalize();
        var center = f + up * ((focusY - height / 2) * worldPerDip);
        camCenter = center;
        camUp = up;
        camRight = Vector3D.CrossProduct(viewDir, up);
        camRight.Normalize();
        camWpd = worldPerDip;

        Viewport = new Viewport3D { Width = width, Height = height };
        Viewport.Camera = new OrthographicCamera(center - viewDir * 5000, viewDir, up, width * worldPerDip)
        {
            NearPlaneDistance = 1, FarPlaneDistance = 20000
        };
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(140, 136, 140)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(160, 155, 150), new Vector3D(-0.3, 1, -0.6)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(80, 80, 100), new Vector3D(0.4, -1, -0.2)));
        scene.Children.Add(body);
        Viewport.Children.Add(new ModelVisual3D { Content = scene });
        if (lying)
        {
            // The book she reads while lying on the taskbar
            bookModel = BuildBook();
            var bt = new Transform3DGroup();
            bt.Children.Add(bookScale);
            bt.Children.Add(bookPlace);
            bookModel.Transform = bt;
            bookVisual = new ModelVisual3D();
            Viewport.Children.Add(bookVisual);
        }
        Children.Add(Viewport);

        Loaded += (s, e) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (s, e) => CompositionTarget.Rendering -= OnFrame;
    }

    public double Now { get { return clock.Elapsed.TotalSeconds; } }
    public void Nod() { nodAt = Now; }
    public void Shake() { shakeAt = Now; }
    public void Hop() { hopAt = Now; }
    public void Cheer() { React("cheer", 1.6); hopAt = Now; }
    readonly Random rnd = new Random();

    // Pose her at time t right away (used for off-screen test renders)
    public void ApplyNow(double t) { Pose(t, 0); if (!lying) Model.Update(); }

    // Advance the animation to time t as if frames had been drawn up to then (for test renders)
    public void Step(double t)
    {
        double dt = lastFrame < 0 ? 0 : t - lastFrame;
        lastFrame = t;
        Pose(t, dt);
        if (!lying) Model.Update();
    }

    void OnFrame(object sender, EventArgs e)
    {
        if (!IsVisible) return;
        Ticks++;
        double t = Now;
        // Fps is a ceiling, not a target. WPF offers a chance to draw ("tick") somewhere between 70
        // and 190 times a second on this PC, and that rate wanders. A cap of 60 made her draw on
        // every second tick, so she got half of whatever was on offer (measured: 58 of 116, 35 of
        // 69) and was no steadier for it. With a high ceiling she draws on every tick she's given.
        double fps = lying && Moving ? Math.Max(Fps, 144) : Fps;
        if (t - lastFrame < 1.0 / fps - 0.002) return;
        double dt = lastFrame < 0 ? 0 : Math.Min(0.1, t - lastFrame);
        lastFrame = t;
        Frames++;
        Pose(t, dt);
        if (!lying) Model.Update();
    }

    void Set(string joint, double rx, double ry, double rz)
    {
        var j = Model[joint];
        if (j == null) return;
        j.Rx = rx; j.Ry = ry; j.Rz = rz;
    }

    void Pose(double t, double dt)
    {
        talkSmooth += (Talk - talkSmooth) * Math.Min(1, dt * 18);
        if (lying) PoseBuddy(t, dt);
        else PoseStand(t, dt, Math.Sin(t * 1.9));
    }

    // Where her head is on screen (in this view's coordinates)
    public Point HeadPoint() { return ScreenPoint("bone009"); }

    // The box her drawn body fills inside the view, in screen units (tests only)
    public Rect Extent()
    {
        var box = Rect.Empty;
        foreach (var q in Model.SomePoints())
        {
            var d = bodyT.Transform(q) - camCenter;
            box.Union(new Point(Width / 2 + Vector3D.DotProduct(d, camRight) / camWpd, Height / 2 - Vector3D.DotProduct(d, camUp) / camWpd));
        }
        return box;
    }

    // How high the lowest bit of her body within `radius` of a joint is (the underside of a knee, the sole
    // of a boot); with no joint, the lowest bit of her overall: the floor (tests and the pose search)
    public double LowestNear(string joint, double radius)
    {
        var c = joint == null ? new Point3D() : WorldPoint(joint);
        double low = double.MaxValue;
        foreach (var q in Model.SomePoints())
        {
            var w = bodyT.Transform(q);
            if (w.Z < low && (joint == null || (w - c).Length < radius)) low = w.Z;
        }
        return low;
    }

    // Where a joint is in the 3D scene, after her pose, turn, swing and lift
    public Point3D WorldPoint(string joint) { return bodyT.Transform(Model.JointPos(joint)); }

    public Point ScreenPoint(string joint)
    {
        var p = bodyT.Transform(Model.JointPos(joint));
        var d = p - camCenter;
        return new Point(Width / 2 + Vector3D.DotProduct(d, camRight) / camWpd, Height / 2 - Vector3D.DotProduct(d, camUp) / camWpd);
    }

    // ---------- Standing (next to the quiz): idle poses, reactions and talking gestures ----------
    string reactPose, idlePose = "rest", talkPose = "explain1";
    double reactAt = -9, reactDur, nextIdleSwap = 4, nextTalkSwap;
    static readonly string[] IdlePoses = { "rest", "rest", "think", "cross", "hips" };
    static readonly string[] LeanIdlePoses = { "rest", "rest", "think", "cross" };

    // Leaning forward on the bottom edge of the screen, forearms resting on it (the quiz look)
    public bool Leaning;
    static readonly string[] TalkPoses = { "explain1", "explain2", "explain3" };
    static readonly string[] StandJoints = { "bone002", "bone009", "bone026", "bone032", "bone034", "bone036" };

    // Hold a pose for a moment: "point", "fist", "facepalm", "cheer", "think"...
    public void React(string pose, double seconds)
    {
        reactPose = pose;
        reactAt = Now;
        reactDur = seconds;
    }

    // Quiz smoothing: the pose eases in two stages (so changes start and stop gently); quick wiggles
    // like talking bobs, nods and fist pumps ride on top with only light smoothing.
    readonly Dictionary<string, double[]> mid = new Dictionary<string, double[]>();
    readonly Dictionary<string, double[]> wigT = new Dictionary<string, double[]>();
    readonly Dictionary<string, double[]> wigC = new Dictionary<string, double[]>();
    double turnMid, turnSm;

    void Wig(string joint, double rx, double ry, double rz)
    {
        double[] v;
        if (!wigT.TryGetValue(joint, out v)) wigT[joint] = v = new double[3];
        v[0] = rx; v[1] = ry; v[2] = rz;
    }

    void PoseStand(double t, double dt, double breathe)
    {
        foreach (var j in StandJoints) { Tgt(j, 0, 0, 0); Wig(j, 0, 0, 0); }
        string p;
        if (t - reactAt < reactDur) p = reactPose;
        else if (Talking)
        {
            if (t >= nextTalkSwap)
            {
                string next;
                do next = TalkPoses[rnd.Next(TalkPoses.Length)]; while (next == talkPose);
                talkPose = next;
                nextTalkSwap = t + 1.3 + rnd.NextDouble() * 1.2;
            }
            p = talkPose;
        }
        else
        {
            if (t >= nextIdleSwap)
            {
                var pool = Leaning ? LeanIdlePoses : IdlePoses;
                idlePose = pool[rnd.Next(pool.Length)];
                nextIdleSwap = t + 6 + rnd.NextDouble() * 6;
            }
            p = idlePose;
        }

        double hy = 0, hp = 0, hr = 0; // extra head angles a pose wants
        double s1 = Math.Sin(t * 3.1), s2 = Math.Sin(t * 2.7 + 1);
        // Arms default to hanging relaxed (or resting on the edge when leaning), breathing slightly
        if (Leaning)
        {
            Tgt("bone002", 0, -15 + 1.2 * breathe, 0);
            Tgt("bone026", 0, -50, 25 + breathe);
            Tgt("bone032", 0, -50, -25 - breathe);
            Tgt("bone034", 80, 0, 0);
            Tgt("bone036", -80, 0, 0);
        }
        else
        {
            Tgt("bone026", 0, 0, 38 + 1.5 * breathe);
            Tgt("bone032", 0, 0, -38 - 1.5 * breathe);
            Tgt("bone034", 12, 0, 0);
            Tgt("bone036", -12, 0, 0);
        }
        ArmPose(p, Tgt, ref hy, ref hp, ref hr);
        switch (p)
        {
            case "fist":
                Wig("bone034", 15 * Math.Sin(t * 12), 0, 0);
                break;
            case "explain1":
                Tgt("bone026", 0, -15, 38); Tgt("bone034", 75, 0, 0); Wig("bone034", 10 * s1, 0, 0);
                break;
            case "explain2":
                Tgt("bone026", 0, -15, 38); Tgt("bone034", 65, 0, 0); Wig("bone034", 8 * s1, 0, 0);
                Tgt("bone032", 0, -15, -38); Tgt("bone036", -65, 0, 0); Wig("bone036", -8 * s2, 0, 0);
                break;
            case "explain3":
                Tgt("bone032", 0, -15, -38); Tgt("bone036", -75, 0, 0); Wig("bone036", -10 * s2, 0, 0);
                break;
        }

        // Head: slow looking around is part of the pose; talking bobs, nods and shakes ride on top
        double yaw = 7 * Math.Sin(t * 0.35) + hy;
        double pitch = 2 * Math.Sin(t * 0.8) + hp + (Leaning ? 10 : 0);
        double roll = 2.5 * Math.Sin(t * 0.27) + hr;
        if (Looking) { yaw *= 0.15; pitch = 3; }
        Tgt("bone009", yaw, pitch, roll); // head: Rx turns, Ry nods (+ = chin up), Rz tilts
        double wy = Talking ? 6 * Math.Sin(t * 1.7) : 0, wr = Talking ? 3 * Math.Sin(t * 1.3) : 0, wp = -talkSmooth * 11;
        double since = t - nodAt;
        if (since < 0.9) wp -= 13 * Math.Abs(Math.Sin(since / 0.9 * Math.PI * 2));
        since = t - shakeAt;
        if (since < 1.0) wy += 18 * Math.Sin(since * Math.PI * 6) * (1 - since);
        Wig("bone009", wy, wp, wr);

        // Two easing stages: a new pose starts slowly, moves, then settles (no snapping or jerks)
        double a = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 9);
        double aw = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 16);
        foreach (var j in StandJoints)
        {
            var target = tgt[j];
            var wig = wigT[j];
            double[] m, c, w;
            if (!mid.TryGetValue(j, out m)) mid[j] = m = (double[])target.Clone();
            if (!cur.TryGetValue(j, out c)) cur[j] = c = (double[])target.Clone();
            if (!wigC.TryGetValue(j, out w)) wigC[j] = w = (double[])wig.Clone();
            for (int i = 0; i < 3; i++)
            {
                m[i] += (target[i] - m[i]) * a;
                c[i] += (m[i] - c[i]) * a;
                w[i] += (wig[i] - w[i]) * aw;
            }
            Set(j, c[0] + w[0], c[1] + w[1], c[2] + w[2]);
        }

        since = t - hopAt;
        lift.OffsetZ = since < 0.8 ? 45 * Math.Abs(Math.Sin(since / 0.8 * Math.PI * 2)) * (1 - since / 1.6) : 0;
        double wantTurn = (Leaning ? 2 : 5) * Math.Sin(t * 0.3) + (p == "point" ? 12 : 0);
        if (dt <= 0) turnMid = turnSm = wantTurn;
        turnMid += (wantTurn - turnMid) * a;
        turnSm += (turnMid - turnSm) * a;
        turn.Angle = turnSm;
    }

    void Tgt(string joint, double rx, double ry, double rz)
    {
        double[] v;
        if (!tgt.TryGetValue(joint, out v)) tgt[joint] = v = new double[3];
        v[0] = rx; v[1] = ry; v[2] = rz;
    }

    // The arm angles (and extra head angles) of each reaction pose. The quiz and the taskbar both use
    // this, each passing in its own way of aiming a joint, so a pose is only written down once.
    // hands on hips: upper arm (three angles) and elbow (found by a search: elbows out beside her, wrists at her waist, no part of
    // either arm inside her body; the old angles put her forearms through her waist)
    static readonly double[] HipsL = { -17, 10, 15, 76 }, HipsR = { 10, 23, 21, -78 };

    static void ArmPose(string pose, Action<string, double, double, double> set, ref double hy, ref double hp, ref double hr)
    {
        switch (pose)
        {
            case "think":
                set("bone026", 0, -55, 30); set("bone034", 150, 0, 0);
                set("bone032", 0, -20, -45); set("bone036", -100, 0, 0);
                hp = 8; hy = 10; hr = 6;
                break;
            case "cross":
                set("bone026", 30, -30, 45); set("bone034", 115, 0, 0);
                set("bone032", 30, -30, -45); set("bone036", -115, 0, 0);
                hr = -4;
                break;
            case "hips":
                set("bone026", HipsL[0], HipsL[1], HipsL[2]); set("bone034", HipsL[3], 0, 0);
                set("bone032", HipsR[0], HipsR[1], HipsR[2]); set("bone036", HipsR[3], 0, 0);
                hr = 5;
                break;
            case "facepalm":
                set("bone026", 0, -80, 15); set("bone034", 128, 0, 0);
                hp = -25; hy = -10;
                break;
            case "sneeze":
                // hand up to her face, head ducking forward
                set("bone026", 0, -64, 32); set("bone034", 144, 0, 0);
                hp = -24; hr = -4;
                break;
            case "yawn":
                // a hand up over her mouth, chin up, head tipped a little to one side
                // (the "think" arm brings a hand to her chin; a little higher and it covers her mouth)
                set("bone026", 0, -64, 32); set("bone034", 146, 0, 0);
                hp = 26; hr = 6;
                break;
            case "point":
                set("bone026", 0, -30, -60); set("bone034", 5, 0, 0);
                hy = 25;
                break;
            case "fist":
                set("bone026", 0, -30, 22); set("bone034", 118, 0, 0); // (less across her: her fist used to pass into her other arm)
                hp = 6;
                break;
            case "cheer":
                set("bone026", 0, 0, -57); set("bone034", 72, 0, 0);
                set("bone032", 0, 0, 57); set("bone036", -72, 0, 0);
                hp = 10;
                break;
        }
    }

    // ---------- Taskbar moments ----------
    // A moment is a few seconds of acting on top of the pose she's settled in: one of the reaction
    // poses above (started with React), or a taskbar-only one: "glance" (looks one way, then the
    // other), "stretch", or "shift" (shifts her weight). It fades in and out so it never snaps, and
    // it's dropped the instant she starts moving to another pose.
    public int MomentDir = 1; // which way she glances first and which leg she rests: +1 = screen right
    public double Jump;       // 0..1: how far into the tucked, in-the-air pose she is (the taskbar window sets this during a jump)

    // Held: picked up (or falling). She goes limp and dangles, facing you, and swings.
    // SwayTarget is how far the carrying is tilting her (degrees, + = feet toward screen left).
    public bool Held;
    public double SwayTarget;
    double heldAmt, heldMid, sway, swayVel;
    readonly AxisAngleRotation3D swayRot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
    RotateTransform3D pitchT, turnT, swayT;
    public double DebugSway { set { sway = value; swayVel = 0; } }

    // Which part of her you've got hold of. She hangs from that joint: by the head or body she
    // stays upright, by a hand she dangles from that arm, by a leg she swings upside down.
    string grabJoint = "bone009";
    static readonly string[] Grabbable = { "bone009", "bone006", "bone003", "bone039", "bone041", "bone007", "bone008", "bone021", "bone027" };

    // Take hold of the joint nearest a point in this view (null = no particular spot: by the head)
    public void Grab(Point? at)
    {
        grabJoint = "bone009";
        if (!at.HasValue) return;
        double best = double.MaxValue;
        foreach (var j in Grabbable)
        {
            var q = ScreenPoint(j);
            double far = (q.X - at.Value.X) * (q.X - at.Value.X) + (q.Y - at.Value.Y) * (q.Y - at.Value.Y);
            if (far < best) { best = far; grabJoint = j; }
        }
        if (grabJoint == "bone003") grabJoint = "bone006"; // round her middle: carried by the chest
    }

    public string Grabbed { get { return grabJoint; } set { grabJoint = value; } }
    public double HeldAmount { get { return heldAmt; } } // 0 = on her own, 1 = fully dangling
    double chestHigh; // how far above the soles of her boots her chest is when she stands
    public double ChestHigh { get { return chestHigh / camWpd; } } // ...in screen units (worked out when asked: the camera is set up after she's measured)

    // Where the held joint is on screen right now (the taskbar window keeps this under the mouse)
    public Point GrabPoint() { return ScreenPoint(grabJoint); }

    // A point of her body after she's been pitched and turned, before the swing and the lift
    Point3D Posed(Point3D q) { return turnT.Transform(lying ? pitchT.Transform(q) : q); }

    // The swing. It pivots on the joint she's held by, and its resting angle is whatever hangs her
    // middle straight below that joint (so held by a foot, upside down). It's a spring: pulled
    // toward that angle plus the tilt the carrying gives her, it overshoots and settles.
    void Swing(double dt)
    {
        if (swayT == null) return;
        var pivot = Posed(Model.JointPos(grabJoint));
        swayT.CenterX = pivot.X; swayT.CenterY = pivot.Y; swayT.CenterZ = pivot.Z;
        double rest = 0;
        if (Held)
        {
            var down = Posed(new Point3D(0, hipY, hipZ + 70)) - pivot; // from the held joint to her middle
            double x = Vector3D.DotProduct(down, camRight), y = Vector3D.DotProduct(down, camUp), far = Math.Sqrt(x * x + y * y);
            // (held close to her middle there's no clear "below", so the pull fades out there)
            rest = Math.Atan2(x, -y) * 180 / Math.PI * Math.Max(0, Math.Min(1, (far - 40) / 100));
            while (rest - sway > 180) rest -= 360; // the short way round
            while (rest - sway < -180) rest += 360;
        }
        else if (heldAmt < 0.01) { sway = 0; swayVel = 0; }
        double step = Math.Min(dt, 0.05);
        swayVel += (((Held ? rest + SwayTarget : 0) - sway) * 70 - swayVel * 5) * step;
        sway += swayVel * step;
        swayRot.Angle = sway * heldAmt;
    }

    // Which moments look right in which pose. Lying down her arms are busy holding the book, so only
    // the two that leave her arms alone work there.
    public static bool Fits(string moment, string pose)
    {
        if (pose == "lie") return moment == "glance" || moment == "stretch";
        if (moment == "shift") return pose == "stand"; // sitting, leaning back put her hands below the edge
        if (moment == "hips") return pose == "stand";  // sitting, her hands ended up in her thighs
        return pose == "stand" || pose == "sit";
    }

    void ApplyMoment(BPose p, double t)
    {
        double age = t - reactAt;
        if (reactPose == null || age < 0 || age >= reactDur + GestureLag || Held || !Fits(reactPose, state)) return;
        // How much of the moment shows: fades 0 -> 1 at the start and back to 0 at the end. The upper arm
        // leads; the forearm, the hand and the head follow a moment behind it, as a real arm moves.
        double fade = Math.Min(0.7, reactDur / 3);
        Func<double, double> wAt = a => a <= 0 || a >= reactDur ? 0 : Ease(Math.Min(1, a / fade)) * Ease(Math.Min(1, (reactDur - a) / fade));
        double w = wAt(age);
        Action<string, double, double, double> to = (j, rx, ry, rz) =>
        {
            var v = p.J[j];
            double wj = wAt(age - (j == "bone034" || j == "bone036" ? 0.06 : j == "bone039" || j == "bone041" ? GestureLag : 0));
            v[0] += (rx - v[0]) * wj; v[1] += (ry - v[1]) * wj; v[2] += (rz - v[2]) * wj;
        };
        double hy = 0, hp = 0, hr = 0;
        int d = MomentDir;
        bool lie = state == "lie";
        switch (reactPose)
        {
            case "glance":
                // Lying down: she looks up from her book for a moment (the same look as noticing you)
                if (lie) ReadingHead(p, t, Math.Max(lookAmt, w));
                else { hy = 36 * d * Math.Sin(2 * Math.PI * age / reactDur); hp = 3; }
                break;
            case "stretch":
                if (lie)
                {
                    // Legs out long and toes pointed, head tipped back
                    to("bone007", 0, 10, 0); to("bone008", 0, 14, 0);
                    to("bone021", 0, 45, 0); to("bone027", 0, 45, 0);
                    hp = 8;
                }
                else
                {
                    // Arms up over her head, back arched, chin up
                    to("bone026", 0, -158, 28); to("bone034", 40, 0, 0);
                    to("bone032", 0, -158, -28); to("bone036", -40, 0, 0);
                    to("bone002", 0, 8, 0);
                    hp = 14;
                }
                break;
            case "wave":
                {
                    // One arm up, the forearm swinging side to side at the elbow, head tipped toward you
                    to("bone026", WaveArm[0], WaveArm[1], WaveArm[2]);
                    to("bone034", WaveArm[3] + 28 * Math.Sin(age * 11), 0, 0);
                    hr = 6; hp = 4;
                }
                break;
            case "dizzy":
                {
                    // Landed from a hard throw: her head circles, her arms come out a little for balance,
                    // and her body sways with it
                    double ph = age * 7;
                    hy = 14 * Math.Sin(ph); hr = 9 * Math.Cos(ph); hp = -4;
                    to("bone026", -30, 0, 19); to("bone032", 30, 0, 4); // (halfway from hanging relaxed to the balancing pose)
                    Add(p, "bone002", 0, 0, 4 * Math.Sin(ph + 1) * w);
                }
                break;
            case "shift":
                {
                    // Weight onto one leg: the other knee bends and that heel comes up
                    to(d > 0 ? "bone003" : "bone005", 0, -14, 0);
                    to(d > 0 ? "bone007" : "bone008", 0, 28, 0);
                    to(d > 0 ? "bone021" : "bone027", 0, 4, 0);
                    to("bone002", 0, 0, 3 * d);
                    hr = -3 * d;
                }
                break;
            default:
                ArmPose(reactPose, to, ref hy, ref hp, ref hr);
                if (reactPose == "fist") Add(p, "bone034", 15 * Math.Sin(t * 12) * w, 0, 0);
                break;
        }
        double wh = wAt(age - 0.08);
        Add(p, "bone009", hy * wh, hp * wh, hr * wh);
    }

    const double GestureLag = 0.12; // how far behind the upper arm her hand follows in a gesture (seconds)

    // ---------- Taskbar engine ----------
    int side = 1;               // which way she faces on the floor: +1 = head toward the right of the screen
    public int NextSide = 1;    // the side she'll use the next time she gets down on the floor
    double lookMid, lookAmt, kickPh, swingPh, walkPh, walkBlend, climbPh, climbBlend, segEndAt, dozeAmt;
    public bool Dozing; // bedtime: lying or sitting, she drifts off (see PoseFor "lie" and "sit")
    double headLead; // how far her head is turned ahead of her body (degrees)
    double bookShut, armsFold, headDown; // the steps of falling asleep, each 0 -> 1 (worked out from dozeAmt)

    // Her mood in how she moves (set by the taskbar window): Energy speeds up or slows down her leg
    // swinging and kicking (1 = normal), and Tapping (impatient) taps a foot now and then when standing
    public double Energy = 1;
    public bool Tapping;

    // left upper arm (three angles) and elbow bend for a wave (found by a search for the hand well above
    // the elbow and the elbow out at shoulder height; the elbow bends the other way to point the forearm up)
    static readonly double[] WaveArm = { -80, -60, -30, -50 };

    // Keeping her balance: set by the taskbar window while the window she's sitting or standing on is
    // being dragged around (0 = still, 1 = moving fast), and which way it's going (+1 = right).
    public double Balance; public int BalanceDir = 1;
    double balAmt;
    public double BalanceAmount { get { return balAmt; } }

    // upper arm (three angles) and elbow, left and right (found by a search for the hand furthest out at about shoulder height)
    static readonly double[] BalArm = { -60, 0, 0, 15 }, BalArmR = { 60, 0, 45, -15 };

    // Arms flung out to the sides, leaning against the way she's being carried, head up
    void Balancing(BPose p)
    {
        double k = balAmt, wob = Math.Sin(Now * 9) * 0.25 * k; // (a little wobble while it lasts)
        Toward(p, "bone026", BalArm[0], BalArm[1], BalArm[2], k); Toward(p, "bone034", BalArm[3], 0, 0, k);
        Toward(p, "bone032", BalArmR[0], BalArmR[1], BalArmR[2], k); Toward(p, "bone036", BalArmR[3], 0, 0, k);
        Add(p, "bone002", 0, 0, (-9 * BalanceDir + 6 * wob) * k);
        Add(p, "bone009", 0, 6 * k, (6 * BalanceDir) * k);
    }
    public double DozeAmount { get { return dozeAmt; } }
    public string DebugState { get { return state + (segTo != null ? " -> " + segTo + " " + segK.ToString("0.00") : ""); } }

    // How far x has got from a to b, eased (0 before a, 1 after b)
    static double Phase(double x, double a, double b) { return Ease(Math.Max(0, Math.Min(1, (x - a) / (b - a)))); }

    // Move a joint part of the way (k) from where the pose has it toward these angles
    static void Toward(BPose p, string joint, double rx, double ry, double rz, double k)
    {
        double[] v;
        if (!p.J.TryGetValue(joint, out v)) p.J[joint] = v = new double[3];
        v[0] += (rx - v[0]) * k; v[1] += (ry - v[1]) * k; v[2] += (rz - v[2]) * k;
    }
    bool segIn, segOut, endedMoving;

    // The body part that stays planted during a move: she moves over her feet when kneeling down,
    // over her knees when leaning onto her hands, and over her chest when stretching out to lie down.
    // driftX is how far that has carried her body sideways (it returns to ~0 when she gets back up).
    string[] anchor;
    double anchorStart, driftX;
    static readonly string[] Ankles = { "bone021", "bone027" }, FrontFoot = { "bone027" }, Hands = { "bone039", "bone041" }, Knees = { "bone007", "bone008" };

    static bool Pair(string a, string b, string x, string y) { return (a == x && b == y) || (a == y && b == x); }

    static string[] AnchorFor(string a, string b)
    {
        if (Pair(a, b, "stand", "crouch")) return Ankles;
        if (Pair(a, b, "stand", "halfkneel") || Pair(a, b, "halfkneel", "footfwd")) return FrontFoot;
        if (Pair(a, b, "footfwd", "allfours")) return Hands;
        if (Pair(a, b, "allfours", "lie")) return Knees;
        return null;
    }

    // Screen-horizontal (world X) position of joints after her body's bend and turn
    double AnchorX(string[] joints, double pitch, double yaw)
    {
        double a = pitch * Math.PI / 180, b = yaw * Math.PI / 180, x = 0;
        foreach (var j in joints)
        {
            var q = Model.JointPos(j);
            double y = hipY + (q.Y - hipY) * Math.Cos(a) - (q.Z - hipZ) * Math.Sin(a);
            x += q.X * Math.Cos(b) - y * Math.Sin(b);
        }
        return x / joints.Length;
    }
    public const double StepHz = 0.9;
    // Test renders can override pose values, e.g. "lie.bone026" = { rx, ry, rz } or "lie.pitch" = { 85 }
    public static Dictionary<string, double[]> Tune;

    // 0..1: how much she's walking right now (ramps up and down as she starts and stops)
    public double WalkBlend { get { return walkBlend; } }
    // True while she's changing pose or walking
    public bool Moving { get { return segTo != null || path.Count > 0 || state == "walk" || state == "climb" || Held; } }
    // How fast to move the window so her steps match the ground (DIPs per second)
    public double WalkSpeedDip { get { return walkUnitsPerSec / camWpd; } }
    double walkUnitsPerSec = 300;

    static bool IsFloor(string s) { return s == "halfkneel" || s == "footfwd" || s == "allfours" || s == "lie"; }

    // ----- The stride -----
    // Gait phase u runs 0 to 1: heel strike at 0, the foot is planted until StanceEnd, then it swings.
    // The foot's path is chosen first (planted and sliding back at a steady rate under her, then lifted
    // and swung forward), and the hip, knee and ankle angles are solved to put it there (two-bone IK).
    // Her foot lands fairly close in front of her and stays planted until it's far behind: that's
    // what lets a real walker's back leg straighten out and push, instead of both knees staying bent.
    const double ArmOut = 12; // degrees her arms are held out from her sides while walking
    const double StanceEnd = 0.6, StrideFwd = 92, StrideBack = 148, FootLift = 34, Bob = 11;
    double thighLen = 171, shinLen = 198;
    // Extra ankle angle on top of keeping the foot flat (+ = toes down): she lands on her heel with
    // her toes up, the foot rolls flat, the heel peels up as the leg trails behind and pushes off
    // from the toes, then the toes come back up for the swing. The last key repeats the first (looping).
    static readonly double[][] AnkleKeys =
    {
        new[] { 0, 0.08, 0.40, 0.50, 0.60, 0.68, 0.78, 0.90, 1.0 },
        new[] { -18.0, 0, 0, 12, 40, 22, 0, -15, -18 }
    };

    // How far below her hips the ground is at gait phase u. A real walker's hips ride a smooth wave,
    // twice per stride: lowest as each heel lands (u = 0 and 0.5), highest as she passes over the
    // planted foot. Both legs are solved against this one height, so both feet agree where the
    // ground is and her body never gets shoved up or dropped. (Lowest exactly at heel strike is
    // what lets the leg land nearly straight and then give gently, instead of buckling.)
    double HipHeight(double u)
    {
        return (thighLen + shinLen) * 0.996 - Bob * (1 + Math.Cos(4 * Math.PI * u)) / 2;
    }

    // Where her ankle is (forward of the hip, and how far below it) for gait phase u, foot flat
    void FootAt(double u, out double f, out double d)
    {
        d = HipHeight(u);
        if (u < StanceEnd)
        {
            // Planted: slides back steadily relative to the hip. The knee gives a little as the leg
            // takes her weight and straightens as she passes over it.
            f = StrideFwd - (StrideFwd + StrideBack) * (u / StanceEnd);
            return;
        }
        // Swinging. Forward: a curve that starts from rest at toe-off and arrives already moving
        // back at the ground's speed (v), so the heel doesn't skid or jerk when it lands; it
        // reaches a little past the landing spot and draws back onto it, as a real foot does.
        double k = (u - StanceEnd) / (1 - StanceEnd);
        double v = -(StrideFwd + StrideBack) / StanceEnd * (1 - StanceEnd);
        double k2 = k * k, k3 = k2 * k;
        f = (2 * k3 - 3 * k2 + 1) * -StrideBack + (-2 * k3 + 3 * k2) * StrideFwd + (k3 - k2) * v;
        // Up: lifts at once after toe-off, is highest a quarter of the way through, and is nearly
        // down again well before landing, so the leg has straightened by the time the heel touches
        d -= FootLift * 9.48 * k * (1 - k) * (1 - k) * (1 - k);
    }

    // How far the lowest point of her foot dips below its flat level when the ankle tips by this many
    // degrees (toes down or up). Measured from the model itself when she's created.
    static readonly double[] TipAngles = { -20, -10, 0, 10, 20, 30, 45, 60 };
    readonly double[] tipDrops = new double[8];
    double ankleHigh; // how far her ankle joint sits above the sole of her boot
    double restLow;   // where the soles of her boots are when she stands straight
    double toeLen;    // how far in front of her ankle her toes are

    double FootDrop(double angle)
    {
        angle = Math.Max(TipAngles[0], Math.Min(TipAngles[TipAngles.Length - 1], angle));
        int i = 0;
        while (i < TipAngles.Length - 2 && angle > TipAngles[i + 1]) i++;
        double k = (angle - TipAngles[i]) / (TipAngles[i + 1] - TipAngles[i]);
        return tipDrops[i] + (tipDrops[i + 1] - tipDrops[i]) * k;
    }

    // How far forward of the hip the foot is at gait phase u (wraps around)
    double FootF(double u)
    {
        u -= Math.Floor(u);
        double f, d;
        FootAt(u, out f, out d);
        return f;
    }

    // Just the thigh angle at gait phase u (wraps around, so u can be a little below 0)
    double HipAt(double u)
    {
        u -= Math.Floor(u);
        double hip, knee, ankle;
        LegAt(u, out hip, out knee, out ankle);
        return hip;
    }

    // Hip (+ = thigh forward), knee (+ = bent) and ankle angles for gait phase u, in degrees
    void LegAt(double u, out double hip, out double knee, out double ankle)
    {
        double f, d;
        FootAt(u, out f, out d);
        // When the foot tips (heel up at push-off, toes up at heel strike) the ankle rides up by
        // however far the toe or heel would otherwise dig in, so the foot stays on the ground
        double tip = Spline(AnkleKeys, u);
        d -= FootDrop(tip);
        // ...and as the heel peels up the foot pivots on its toes, which carries the ankle forward a little
        f += toeLen * (1 - Math.Cos(Math.Max(0, tip) * Math.PI / 180));
        double r = Math.Min(Math.Sqrt(f * f + d * d), thighLen + shinLen - 0.5);
        double cb = (r * r - thighLen * thighLen - shinLen * shinLen) / (2 * thighLen * shinLen);
        double beta = Math.Acos(Math.Max(-1, Math.Min(1, cb)));
        double cg = (thighLen * thighLen + r * r - shinLen * shinLen) / (2 * thighLen * r);
        double gamma = Math.Acos(Math.Max(-1, Math.Min(1, cg)));
        double alpha = Math.Atan2(f, d) + gamma; // the knee sits in front of the hip-to-ankle line
        hip = alpha * 180 / Math.PI;
        knee = beta * 180 / Math.PI;
        // Keeping the foot flat means matching the shin's tilt; the keys add heel strike and push-off
        ankle = (hip - knee) + tip;
    }

    // Smooth looping curve through the keyframes (Catmull-Rom)
    static double Spline(double[][] keys, double u)
    {
        var us = keys[0];
        var vs = keys[1];
        int n = us.Length - 1;
        int i = 0;
        while (i < n - 1 && u > us[i + 1]) i++;
        double k = (u - us[i]) / (us[i + 1] - us[i]);
        double p0 = vs[(i - 1 + n) % n], p1 = vs[i], p2 = vs[(i + 1) % n], p3 = vs[(i + 2) % n];
        return 0.5 * (2 * p1 + (-p0 + p2) * k + (2 * p0 - 5 * p1 + 4 * p2 - p3) * k * k + (-p0 + 3 * p1 - 3 * p2 + p3) * k * k * k);
    }

    // How fast her planted foot moves back relative to her body during a stride. Moving the window
    // at exactly this speed keeps her feet from sliding along the taskbar.
    void CalibrateWalk()
    {
        // First measure her feet: tip one ankle and see how far its toe (or heel) dips below flat
        Model.ResetPose();
        Model.Update();
        double flat = Model.LowestZ(0, hipY, hipZ);
        ankleHigh = Model.JointPos("bone021").Z - flat;
        restLow = flat;
        chestHigh = Model.JointPos("bone006").Z - flat;
        toeLen = tipDrops[4] / Math.Sin(20 * Math.PI / 180); // from how far the toe dips at 20 degrees
        for (int i = 0; i < TipAngles.Length; i++)
        {
            Set("bone021", 0, TipAngles[i], 0);
            Model.Update();
            tipDrops[i] = Math.Max(0, flat - Model.LowestZ(0, hipY, hipZ));
        }
        double save = walkPh;
        double y1 = AnkleYAt(0.1), y2 = AnkleYAt(0.5);
        walkPh = save;
        walkUnitsPerSec = Math.Abs(y2 - y1) / (0.4 / StepHz);
        Model.ResetPose();
        Model.Update();
    }

    double AnkleYAt(double u)
    {
        walkPh = u * 2 * Math.PI;
        var p = PoseFor("walk", 0);
        foreach (var kv in p.J) Set(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2]);
        Model.Update();
        return Model.JointPos("bone021").Y; // left ankle, planted between u = 0 and 0.6
    }

    void PoseBuddy(double t, double dt)
    {
        // Noticing you: two easing stages, so her head turns like a reaction instead of snapping
        double la = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 6);
        lookMid += ((Looking ? 1 : 0) - lookMid) * la;
        lookAmt += (lookMid - lookAmt) * la;
        // Watching the mouse eases the same way: whether she's watching at all, and where it is
        Ease2(ref gazeAmtMid, ref gazeAmt, Watching ? 1 : 0, la);
        Ease2(ref gazeYawMid, ref gazeYaw, Watching ? GazeYaw : 0, la);
        Ease2(ref gazePitchMid, ref gazePitch, Watching ? GazePitch : 0, la);
        kickPh += dt * 1.7 * Energy; // lazy kicking while she reads (livelier when she's in a good mood)
        // Falling asleep is a little routine, played over about 6 seconds: close the book, slide it
        // aside and fold her arms, then lay her head down. Waking plays it backwards, quicker.
        // dozeAmt runs 0 -> 1 at a steady rate, and each step takes its own stretch of it.
        dozeAmt = dt <= 0 ? (Dozing ? 1 : 0) : Math.Max(0, Math.Min(1, dozeAmt + (Dozing ? dt / 6 : -dt / 1.6)));
        bookShut = Phase(dozeAmt, 0, 0.3);
        armsFold = Phase(dozeAmt, 0.25, 0.65);
        headDown = Phase(dozeAmt, 0.55, 1);
        swingPh += dt * 2.6 * Energy * (1 - 0.45 * lookAmt);
        if (state == "walk" || segTo == "walk") walkPh += dt * 2 * Math.PI * StepHz;
        if (state == "climb" || segTo == "climb") climbPh += dt * 2 * Math.PI * 0.8;

        // Finish the move in progress, then start the next one in the plan. Moves in a chain flow
        // into each other without stopping; only the first eases in and the last eases out.
        if (segTo != null && t - segStart >= segDur)
        {
            state = segTo;
            segTo = null;
            endedMoving = segOut;
            segEndAt = segStart + segDur;
        }
        // Before walking off or getting down on the floor she turns to face that way first
        bool turning = false;
        if (segTo == null && path.Count > 0 && state == "stand")
        {
            string n = path.Peek();
            if (n == "walk" || n == "climb" || IsFloor(n))
                turning = Math.Abs(yawNow - (n == "walk" ? 90 * WalkDir : n == "climb" ? 180 * WalkDir : FloorYaw * NextSide)) > 15;
        }
        if (segTo == null && path.Count > 0 && !turning)
        {
            segIn = endedMoving && t - segEndAt < 0.25;
            segTo = path.Dequeue();
            segOut = path.Count > 0;
            if (state == "stand" && IsFloor(segTo)) side = NextSide;
            // The model still holds last frame's pose (= shown), so the planted part's position is known
            anchor = shown != null ? AnchorFor(state, segTo) : null;
            from = shown != null ? shown.Clone() : PoseFor(state, t);
            if (anchor != null) anchorStart = AnchorX(anchor, from.Pitch, yawNow) + driftX;
            segStart = segIn ? segEndAt : t;
            segDur = MoveTime(state, segTo);
            reactDur = 0; // a moment ends when she starts to move (this move starts from it, so no jump)
        }
        if (segTo == null) { endedMoving = false; anchor = null; }

        // Turn smoothly toward wherever she should be facing
        // Picked up: ease into dangling (the swing itself is worked out further down, in Swing)
        Ease2(ref heldMid, ref heldAmt, Held ? 1 : 0, dt <= 0 ? 1 : 1 - Math.Exp(-dt * 14));
        double wantYaw = YawTarget(t) * (1 - heldAmt); // held, she faces you
        if (yawSnap || dt <= 0) { yawMid = yawNow = wantYaw; yawSnap = false; }
        double ya = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 7);
        yawMid += (wantYaw - yawMid) * ya;
        yawNow += (yawMid - yawNow) * ya;

        BPose p;
        walkBlend = state == "walk" ? 1 : 0;
        climbBlend = state == "climb" ? 1 : 0;
        segK = 0;
        if (segTo != null)
        {
            double k = Math.Max(0, Math.Min(1, (t - segStart) / segDur));
            segK = k;
            double e = segIn && segOut ? k : segOut ? k * k * (2 - k) : segIn ? k * (1 + k - k * k) : Ease(k);
            var target = PoseFor(segTo, t);
            p = Lerp(from, target, e);
            if (!segIn) Stagger(p, from, target, k, segOut);
            Arc(p, state, segTo, Math.Sin(Math.PI * k));
            if (segTo == "walk") walkBlend = e;
            else if (state == "walk") walkBlend = 1 - e;
            if (segTo == "climb") climbBlend = e;
            else if (state == "climb") climbBlend = 1 - e;
        }
        else { p = PoseFor(state, t); ApplyMoment(p, t); }
        if (heldAmt > 0.001) p = Lerp(p, PoseFor("hang", t), heldAmt);
        // a jolt throws her arms out at once; she settles back over a second or so
        balAmt += (Balance - balAmt) * (dt <= 0 ? 1 : 1 - Math.Exp(-dt * (Balance > balAmt ? 14 : 2.5)));
        if (balAmt > 0.005 && segTo == null && !Held && (state == "sit" || state == "stand")) Balancing(p);
        if (Jump > 0)
        {
            // In the air: knees tucked up (the leading one more), arms swung forward for the leap
            Add(p, "bone003", 0, -40 * Jump, 0); Add(p, "bone005", 0, -25 * Jump, 0);
            Add(p, "bone007", 0, 65 * Jump, 0); Add(p, "bone008", 0, 45 * Jump, 0);
            Add(p, "bone026", 0, -55 * Jump, 0); Add(p, "bone032", 0, -55 * Jump, 0);
            Add(p, "bone002", 0, -6 * Jump, 0);
        }
        // Turning, she looks first: her head turns toward where she's about to face, up to 30 degrees ahead
        // of her body. The lead itself eases in and out (the direction she's heading can switch in an
        // instant when a move ends, and following it directly made her head snap).
        double leadWant = Math.Max(-30, Math.Min(30, (wantYaw - yawNow) * 0.45));
        headLead += (leadWant - headLead) * (dt <= 0 ? 1 : 1 - Math.Exp(-dt * 3));
        if (Math.Abs(headLead) > 0.1) Add(p, "bone009", headLead, 0, 0);
        shown = p;

        foreach (var kv in p.J) Set(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2]);
        double since = t - nodAt;
        if (since < 0.9)
        {
            var h = Model["bone009"];
            if (h != null) h.Ry -= 13 * Math.Abs(Math.Sin(since / 0.9 * Math.PI * 2));
        }
        since = t - shakeAt;
        if (since < 1.2)
        {
            // Shaking her head (shrugging your hand off): quick turns side to side that die away
            var h = Model["bone009"];
            if (h != null) h.Rx += 20 * Math.Sin(since * Math.PI * 5) * (1 - since / 1.2);
        }
        Model.Update();

        // Keep her on the taskbar: her seat on the edge when sitting, otherwise whatever part of her is lowest
        pitchRot.Angle = p.Pitch;
        turn.Angle = yawNow;
        Swing(dt);
        double low = Model.LowestZ(p.Pitch, hipY, hipZ);
        if (walkBlend > 0)
        {
            // Walking, the ground is wherever the stride says it is under her hips (a smooth wave),
            // not wherever the lowest bit of boot happens to be this frame (which jitters)
            double u = walkPh / (2 * Math.PI);
            double ground = hipZ - HipHeight(u - Math.Floor(u)) - ankleHigh;
            low += (ground - low) * walkBlend;
        }
        if (climbBlend > 0)
        {
            // Climbing, there's no ground under her feet: she hangs at standing height and bobs a
            // little with each pull
            low += (restLow + 6 * Math.Cos(2 * climbPh) - low) * climbBlend;
        }
        double seat = hipZ + (seatZ - hipZ) * Math.Cos(p.Pitch * Math.PI / 180);
        lift.OffsetZ = -(p.SeatW * seat + (1 - p.SeatW) * low) - p.Sink;
        // Hanging upside down she's below the part she's held by, where her window has no room, so
        // she's drawn higher up inside it. (The window follows the held part, so on screen that
        // part stays in your hand and nothing appears to jump.)
        lift.OffsetZ += heldAmt * (1 - Math.Cos(sway * Math.PI / 180)) / 2 * 270 * camWpd;

        // Keep the planted part where it was. Once she's upright again, any leftover offset is handed
        // to the window a little at a time (the window moves, she doesn't), so she never slides.
        if (anchor != null) driftX = anchorStart - AnchorX(anchor, p.Pitch, yawNow);
        else if (!IsFloor(state) && dt > 0 && driftX != 0)
        {
            double d = Math.Abs(driftX) < 0.5 ? driftX : driftX * (1 - Math.Exp(-dt * 2));
            driftX -= d;
            if (ShiftWindow != null) ShiftWindow(d / camWpd);
        }
        lift.OffsetX = driftX;

        PlaceBook(t);

        // Let the taskbar window move her along in step with this exact frame
        if (Stepped != null && dt > 0) Stepped(dt);
    }

    // ---------- The book ----------
    // An open hardcover lying on the floor between her hands while she reads. It's placed from her
    // wrists every frame, so it stays in her hands; it shows up as she settles in and goes away when
    // she gets up. Every so often she turns a page.
    Model3DGroup bookModel;
    ModelVisual3D bookVisual;
    readonly ScaleTransform3D bookScale = new ScaleTransform3D(0, 0, 0);
    readonly MatrixTransform3D bookPlace = new MatrixTransform3D();
    readonly AxisAngleRotation3D pageTurn = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -8);
    readonly AxisAngleRotation3D bookHinge = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 8); // 8 = open, 180 = shut
    public Point3D BookAt { get; private set; } // where the book is (tests)
    double segK, bookAmt, pageStart = -100, nextPage = 14;
    const double PageW = 60, PageH = 88, CoverT = 2.5, PagesT = 5;

    void PlaceBook(double t)
    {
        if (bookVisual == null) return;
        double want = 0;
        if (state == "lie" && segTo == null) want = 1;
        else if (segTo == "lie") want = Ease(Math.Max(0, Math.Min(1, (segK - 0.25) / 0.75)));
        else if (state == "lie") want = 1 - Ease(Math.Min(1, segK / 0.6));
        bookAmt = want * (1 - heldAmt); // (she lets go of the book when she's picked up)
        if (bookAmt < 0.01)
        {
            if (bookVisual.Content != null) bookVisual.Content = null;
            return;
        }
        if (bookVisual.Content == null) bookVisual.Content = bookModel;

        // Between her wrists, a little ahead of them, resting on the floor
        var wl = bodyT.Transform(Model.JointPos("bone039"));
        var wr = bodyT.Transform(Model.JointPos("bone041"));
        double yaw = yawNow * Math.PI / 180;
        var ahead = new Vector3D(Math.Sin(yaw), -Math.Cos(yaw), 0);  // where her head points
        var across = new Vector3D(Math.Cos(yaw), Math.Sin(yaw), 0);  // toward her left hand
        var c = new Point3D((wl.X + wr.X) / 2, (wl.Y + wr.Y) / 2, Math.Min(wl.Z, wr.Z) - 16) + ahead * 26;
        // At bedtime it's shut, then pushed aside: ahead of her and off to one side of her head
        if (armsFold > 0)
        {
            // (height 0 is the floor her window stands her on; straight ahead, because off to one side
            // the slant of the view made it look like it floated)
            var neck = bodyT.Transform(Model.JointPos("bone006"));
            var aside = new Point3D(neck.X, neck.Y, 0) + ahead * 125;
            c += (aside - c) * armsFold;
        }
        bookHinge.Angle = 8 + 172 * bookShut;
        BookAt = c;
        bookPlace.Matrix = new Matrix3D(across.X, across.Y, across.Z, 0, ahead.X, ahead.Y, ahead.Z, 0, 0, 0, 1, 0, c.X, c.Y, c.Z, 1);
        bookScale.ScaleX = bookScale.ScaleY = bookScale.ScaleZ = bookAmt;

        // Page turns: the page lifts off the right side, arcs over the spine, and settles on the left
        if (state == "lie" && segTo == null && !Looking && !Watching && dozeAmt <= 0 && t >= nextPage && t - pageStart > 2)
        {
            pageStart = t;
            nextPage = t + 15 + rnd.NextDouble() * 12;
        }
        double k = (t - pageStart) / 0.9;
        pageTurn.Angle = k >= 0 && k < 1 ? -8 - 164 * Ease(k) : -8;
    }

    Model3DGroup BuildBook()
    {
        var cover = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x6A, 0x18, 0x14)));
        var edge = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xE2, 0xD8, 0xC2)));
        var book = new Model3DGroup();
        for (int s = -1; s <= 1; s += 2)
        {
            // One half: the cover, then the block of pages on top, tilted up a little from the spine
            var half = new Model3DGroup();
            double x0 = s < 0 ? -PageW - 3 : 0, x1 = s < 0 ? 0 : PageW + 3;
            half.Children.Add(Box(x0, x1, -PageH / 2 - 3, PageH / 2 + 3, 0, CoverT, cover, cover));
            double p0 = s < 0 ? -PageW : 0, p1 = s < 0 ? 0 : PageW;
            half.Children.Add(Box(p0, p1, -PageH / 2, PageH / 2, CoverT, CoverT + PagesT, new DiffuseMaterial(PageBrush(s < 0 ? 3 : 7)), edge));
            // (the left half is hinged at the top of the spine, so at bedtime it can fold over onto the right one: shut)
            half.Transform = s < 0 ? new RotateTransform3D(bookHinge, new Point3D(0, 0, CoverT + PagesT)) : new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), -8));
            book.Children.Add(half);
        }
        // The page that turns, hinged at the spine on top of the pages
        var page = Box(0, PageW - 1, -PageH / 2 + 1, PageH / 2 - 1, 0, 0.6, new DiffuseMaterial(PageBrush(11)), edge);
        page.Transform = new RotateTransform3D(pageTurn, new Point3D(0, 0, 0));
        var lifted = new Model3DGroup();
        lifted.Children.Add(page);
        lifted.Transform = new TranslateTransform3D(0, 0, CoverT + PagesT + 0.3);
        book.Children.Add(lifted);
        // Spine
        book.Children.Add(Box(-2.5, 2.5, -PageH / 2 - 3, PageH / 2 + 3, -0.5, CoverT + 1, cover, cover));
        return book;
    }

    // A cream page with lines of "text"
    static Brush PageBrush(int seed)
    {
        var r = new Random(seed);
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF2, 0xEA, 0xD8)), null, new RectangleGeometry(new Rect(0, 0, 100, 146))));
        var ink = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x4A, 0x40, 0x36)), 2.4);
        for (double y = 16; y < 134; y += 8)
        {
            double len = r.NextDouble() < 0.12 ? 30 + r.NextDouble() * 30 : 70 + r.NextDouble() * 10;
            dg.Children.Add(new GeometryDrawing(null, ink, new LineGeometry(new Point(12, y), new Point(12 + len, y))));
        }
        var b = new DrawingBrush(dg) { Stretch = Stretch.Fill };
        b.Freeze();
        return b;
    }

    static GeometryModel3D Quad(Point3D a, Point3D b, Point3D c, Point3D d, Material mat)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { a, b, c, d },
            TextureCoordinates = new PointCollection { new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };
        return new GeometryModel3D(mesh, mat) { BackMaterial = mat };
    }

    // A box with its own material on top (the printed page) and another on the sides
    static Model3DGroup Box(double x0, double x1, double y0, double y1, double z0, double z1, Material top, Material sides)
    {
        var g = new Model3DGroup();
        g.Children.Add(Quad(new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1), top));
        g.Children.Add(Quad(new Point3D(x0, y1, z0), new Point3D(x1, y1, z0), new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), sides));
        g.Children.Add(Quad(new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x0, y0, z1), sides));
        g.Children.Add(Quad(new Point3D(x1, y1, z0), new Point3D(x0, y1, z0), new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), sides));
        g.Children.Add(Quad(new Point3D(x0, y1, z0), new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), sides));
        g.Children.Add(Quad(new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x1, y1, z1), new Point3D(x1, y0, z1), sides));
        return g;
    }

    // Called after every taskbar frame with its time step (the window walks her with this)
    public Action<double> Stepped;

    public double DebugDrift { get { return driftX / camWpd; } }
    public double DebugLift { get { return lift.OffsetZ / camWpd; } } // how high her body is held, in screen units
    // Through one walking stride (12 steps): how far out to the side each wrist is from the middle of
    // her body, next to how far out her hip joint is. A wrist closer in than the hip is inside her.
    public string DebugArms()
    {
        var sb = new System.Text.StringBuilder();
        string was = state;
        double keep = walkPh;
        var rows = new[] { new System.Text.StringBuilder("left wrist out "), new System.Text.StringBuilder("right wrist out"), new System.Text.StringBuilder("wrist fwd (L)  "), new System.Text.StringBuilder("wrist below hip") };
        for (int i = 0; i < 12; i++)
        {
            walkPh = i / 12.0 * 2 * Math.PI;
            var pose = PoseFor("walk", 0);
            foreach (var kv in pose.J) Set(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2]);
            Model.UpdateJoints();
            Point3D l = Model.JointPos("bone039"), r = Model.JointPos("bone041"), hip = Model.JointPos("bone003");
            rows[0].Append(Math.Abs(l.X).ToString("0").PadLeft(5));
            rows[1].Append(Math.Abs(r.X).ToString("0").PadLeft(5));
            rows[2].Append((hip.Y - l.Y).ToString("0").PadLeft(5));
            rows[3].Append((hip.Z - l.Z).ToString("0").PadLeft(5));
        }
        walkPh = keep;
        var hipOut = Math.Abs(Model.JointPos("bone003").X);
        foreach (var row in rows) sb.Append(Environment.NewLine + "   " + row);
        return " (hip joint is " + hipOut.ToString("0") + " out)" + sb;
    }

    // Hip, knee and ankle angles through one stride (24 steps), for checking the walk against a real one
    public string DebugGait()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var name in new[] { "hip", "knee", "ankle" })
        {
            sb.Append(Environment.NewLine + "   " + name.PadRight(6));
            for (int i = 0; i < 24; i++)
            {
                double h, k, a;
                LegAt(i / 24.0, out h, out k, out a);
                sb.Append((name == "hip" ? h : name == "knee" ? k : a - (h - k)).ToString("0").PadLeft(4));
            }
        }
        return sb.ToString();
    }
    public string DebugFoot { get { return string.Join(" ", tipDrops.Select(d => (d / camWpd).ToString("0.0"))); } }
    public double DebugYaw { get { return yawNow; } }

    // The taskbar window moves by this many DIPs (see above)
    public Action<double> ShiftWindow;

    static void J(BPose p, string joint, double rx, double ry, double rz) { p.J[joint] = new[] { rx, ry, rz }; }

    static void Add(BPose p, string joint, double rx, double ry, double rz)
    {
        double[] v;
        if (!p.J.TryGetValue(joint, out v)) p.J[joint] = v = new double[3];
        v[0] += rx; v[1] += ry; v[2] += rz;
    }

    // Head angles: idle looking around, blending into looking at you while you hover over her.
    // The head bone turns with Rx (+ = toward her left), nods with Ry (+ = chin up) and tilts with Rz.
    // Watching the mouse turns her head further, toward where it is (hovering wins: then she looks at you).
    void Head(BPose p, double yaw, double pitch, double roll, double lYaw, double lPitch, double lRoll)
    {
        double L = lookAmt;
        J(p, "bone009", yaw + (lYaw - yaw) * L + gazeYaw * (1 - L), pitch + (lPitch - pitch) * L + gazePitch * (1 - L), roll + (lRoll - roll) * L);
    }

    // Watching something on screen (the taskbar window points this at the mouse). The angles are how
    // far her head should turn to face it, in degrees: + = toward the right of the screen / upward.
    public bool Watching;
    public double GazeYaw, GazePitch;
    double gazeAmt, gazeAmtMid, gazeYaw, gazeYawMid, gazePitch, gazePitchMid;

    // Two easing stages in a row: "now" chases "mid", which chases the wanted value. The result
    // starts gently and stops gently.
    static void Ease2(ref double mid, ref double now, double want, double a)
    {
        mid += (want - mid) * a;
        now += (mid - now) * a;
    }

    // The full pose for each state at time t (idle motion included)
    BPose PoseFor(string s, double t)
    {
        var p = new BPose { Sink = 3 };
        foreach (var j in BuddyJoints) p.J[j] = new double[3];
        double br = Math.Sin(t * 1.9), L = lookAmt;
        switch (s)
        {
            case "lie":
            {
                // Reading on her stomach: propped on her elbows, forearms on the floor holding an open
                // book between her hands, eyes on the page, feet up and kicking lazily.
                // Noticing you: she lifts her head from the book and looks over at you.
                // Dozing (bedtime): the kicking stops and her feet sink, her breathing slows and
                // deepens, and her head drops onto her arms. Looking up at you undoes the head part.
                // Falling asleep (see dozeAmt): the book shuts, her feet stop kicking and come down, she
                // pushes the book aside and folds her arms, then sinks onto them and lays her head down,
                // breathing slow and deep.
                double sleep = headDown, kick = 22 * (1 - armsFold);
                Propped(p, 0.2 * sleep); // (asleep she sinks a little; all the way flat put her elbows below the floor)
                ReadingArms(p, br * (1 - sleep) + 1.8 * Math.Sin(t * 0.9) * sleep);
                FoldedArms(p, armsFold);
                J(p, "bone007", 0, 80 - 62 * armsFold + kick * Math.Sin(kickPh), 0);
                J(p, "bone008", 0, 80 - 56 * armsFold - kick * Math.Sin(kickPh + 0.9), 0);
                J(p, "bone021", 0, 22 + 20 * sleep, 0); J(p, "bone027", 0, 22 + 20 * sleep, 0); // toes pointed, relaxed
                // The mouse coming near also gets her to look up from the page, turning a little with it
                double look = Math.Max(L, gazeAmt);
                ReadingHead(p, t, look, 1 - armsFold);
                Add(p, "bone009", 0.3 * gazeYaw * (1 - L), 0, 0);
                double nod = sleep * (1 - look);
                Add(p, "bone006", 0, -7 * nod, 0); // (a smaller angle here bows her head; any further and her face sinks into her arms)
                Add(p, "bone009", 0, -6 * nod, -16 * side * nod); // ...and her cheek turns down onto her arms
                PropHead(p, look);
                break;
            }
            case "allfours":
            {
                // Hands and knees: the in-between pose for getting down onto the floor and back up
                p.Pitch = 78; p.Sink = 4;
                J(p, "bone002", 0, 4 + br, 0);
                J(p, "bone003", 0, -78, 0); J(p, "bone005", 0, -78, 0);
                J(p, "bone007", 0, 90, 0); J(p, "bone008", 0, 90, 0);
                J(p, "bone021", 0, 70, 0); J(p, "bone027", 0, 70, 0);
                J(p, "bone026", -40, -70, 10); J(p, "bone032", -40, -70, -10);
                J(p, "bone034", 12, 0, 0); J(p, "bone036", -12, 0, 0);
                Head(p, 0, 26 + 2 * Math.Sin(t * 0.8), -32 * side, -65 * side, 40, -20 * side);
                break;
            }
            case "footfwd":
            {
                // Hands still on the floor, one foot (her right) brought forward and planted between them,
                // the other knee still down: the step between hands-and-knees and kneeling up on one knee
                // (angles found by a search: front foot well ahead and flat, back knee and hands on the floor, back
                // thigh straight down, nothing passing through her body)
                p.Pitch = 57; p.Sink = 4;
                J(p, "bone002", 0, 4 + br, 0);
                J(p, "bone003", 0, -53, 0); J(p, "bone007", 0, 90, 0); J(p, "bone021", 0, 77, 0);
                J(p, "bone005", 0, -168, 0); J(p, "bone008", 0, 84, 0); J(p, "bone027", 0, -20, 0);
                J(p, "bone026", 41, -27, 10); J(p, "bone032", FootFwdR[0], FootFwdR[1], FootFwdR[2]);
                J(p, "bone034", -7, 0, 0); J(p, "bone036", FootFwdR[3], 0, 0);
                foreach (var sk in new[] { "bone016", "bone017" }) J(p, sk, 0, 60, 0);
                Head(p, 0, 22 + 2 * Math.Sin(t * 0.8), -20 * side, -50 * side, 30, -12 * side);
                break;
            }
            case "halfkneel":
            {
                // Up on one knee: the back knee and shin on the floor, the front foot flat with its shin
                // straight up, her body upright, a hand on the front knee ready to push off it
                // (angles found by the pose search: front foot, back knee and back toes on the floor)
                p.Pitch = 8; p.Sink = 4;
                J(p, "bone003", 0, -7, 0); J(p, "bone007", 0, 86, 0); J(p, "bone021", 0, 77, 0);
                J(p, "bone005", 0, -125, 0); J(p, "bone008", 0, 112, 0); J(p, "bone027", 0, -7, 0);
                foreach (var sk in new[] { "bone016", "bone017" }) J(p, sk, 0, 60, 0);
                J(p, "bone026", 0, -20, 36 + br); J(p, "bone034", 25, 0, 0);
                J(p, "bone032", HalfKneelR[0], HalfKneelR[1], HalfKneelR[2]); J(p, "bone036", HalfKneelR[3], 0, 0);
                Head(p, (-15 + 4 * Math.Sin(t * 0.35)) * side, 2 * Math.Sin(t * 0.8), 2 * Math.Sin(t * 0.27) * side, -50 * side, 10, -6 * side);
                break;
            }
            case "crouch":
            {
                // Squatting on the edge, about to sit down on it (or just getting up)
                p.Pitch = 22;
                J(p, "bone003", 0, -97, 0); J(p, "bone005", 0, -97, 0);
                J(p, "bone007", 0, 115, 0); J(p, "bone008", 0, 115, 0);
                J(p, "bone021", 0, -38, 0); J(p, "bone027", 0, -38, 0);
                foreach (var sk in Skirt) J(p, sk, 0, 60, 0);
                J(p, "bone026", CrouchL[0], CrouchL[1], CrouchL[2]); J(p, "bone032", CrouchR[0], CrouchR[1], CrouchR[2]);
                J(p, "bone034", CrouchL[3], 0, 0); J(p, "bone036", CrouchR[3], 0, 0);
                Head(p, 4 * Math.Sin(t * 0.5), -6 + 2 * Math.Sin(t * 0.8), 0, 0, 14, 0);
                break;
            }
            case "sit":
            {
                // Sitting on the edge, legs swinging over the taskbar. Noticing you: she looks up at you.
                // Nodding off (bedtime): her legs slow to a stop, her hands settle into her lap, and her
                // head droops lower and lower until it jerks back up, then starts drooping again.
                p.SeatW = 1; p.Sink = 0;
                double drowsy = armsFold, sw = Math.Sin(swingPh) * (1 - drowsy);
                J(p, "bone003", 0, -90, 0); J(p, "bone005", 0, -90, 0);
                J(p, "bone007", 0, 86 + 24 * sw, 0); J(p, "bone008", 0, 86 - 24 * sw, 0);
                J(p, "bone021", 0, 12, 0); J(p, "bone027", 0, 12, 0);
                foreach (var sk in Skirt) J(p, sk, 0, 75, 0);
                J(p, "bone026", SitL[0], SitL[1], SitL[2] + br); J(p, "bone032", SitR[0], SitR[1], SitR[2] - br);
                J(p, "bone034", SitL[3], 0, 0); J(p, "bone036", SitR[3] - 15 * drowsy, 0, 0); // (slumping, her right hand would sink into her left thigh: it lifts a little)
                J(p, "bone002", 0, -6 * headDown, 0); // shoulders slump forward (- curls the spine forward)
                J(p, "bone006", 0, -18 * headDown * NodOff(t), 0); // ...and the neck bows with the nod
                Head(p, (14 * Math.Sin(t * 0.31) + 6 * Math.Sin(t * 0.83)) * (1 - drowsy), 2 * Math.Sin(t * 0.8) - 34 * headDown * NodOff(t), 2.5 * Math.Sin(t * 0.27) + 8 * headDown, 0, 16, 5);
                break;
            }
            case "walk":
            {
                // A real stride: each leg plants at heel strike, sweeps back at a steady rate while it
                // carries her (so the foot stays put on the taskbar), pushes off, then swings through
                // with the knee bending most just after toe-off. Her left side is bones 003/007/021/026/034,
                // her right side 005/008/027/032/036; the legs are half a cycle apart, and each arm
                // swings opposite its own leg. The shoulders twist slightly against the hips.
                double u = walkPh / (2 * Math.PI);
                u -= Math.Floor(u);
                double uL = u, uR = (u + 0.5) % 1;
                double hipL, kneeL, ankleL, hipR, kneeR, ankleR;
                LegAt(uL, out hipL, out kneeL, out ankleL);
                LegAt(uR, out hipR, out kneeR, out ankleR);
                p.Pitch = 0;
                J(p, "bone003", 0, -hipL, 0); J(p, "bone005", 0, -hipR, 0);
                J(p, "bone007", 0, kneeL, 0); J(p, "bone008", 0, kneeR, 0);
                J(p, "bone021", 0, ankleL, 0); J(p, "bone027", 0, ankleR, 0);
                // The skirt follows each thigh partway and a moment late, so it sways instead of
                // swinging stiffly with the leg
                double skL = HipAt(uL - 0.07), skR = HipAt(uR - 0.07);
                J(p, "bone012", 0, 0.58 * skL, 0); J(p, "bone013", 0, 0.58 * skL, 0);
                J(p, "bone016", 0, 0.58 * skR, 0); J(p, "bone017", 0, 0.58 * skR, 0);
                // Arms swing opposite their own leg and a little behind it, further forward than back,
                // held slightly away from her sides so the hands clear her hips and skirt. The elbow
                // bends as the arm comes forward and trails the upper arm (follow-through). The swing
                // size drifts a little so no two strides are identical.
                // (driven by where each foot is along the stride, which swings smoothly from back to front)
                double amp = 0.10 * (1 + 0.12 * Math.Sin(t * 0.53) + 0.06 * Math.Sin(t * 1.37));
                double mid = (StrideFwd - StrideBack) / 2; // her feet go further back than forward; the arms swing about the middle of that
                double armL = amp * (FootF(uL - 0.05) - mid) - 6, armR = amp * (FootF(uR - 0.05) - mid) - 6; // + = back
                double elbL = amp * (FootF(uL - 0.14) - mid) - 6, elbR = amp * (FootF(uR - 0.14) - mid) - 6;
                // (the first angle lifts each arm out sideways from her body, enough that her hands
                // swing past her hips and skirt instead of through them)
                J(p, "bone026", -ArmOut, armL, 27 + 0.6 * br); J(p, "bone032", ArmOut, armR, -27 - 0.6 * br);
                J(p, "bone034", 24 + 0.9 * Math.Max(0, -elbL), 0, 0); J(p, "bone036", -(24 + 0.9 * Math.Max(0, -elbR)), 0, 0);
                // Shoulders twist against the hips; the spine leans into the walk and dips with each step
                double twist = 0.04 * (FootF(uL) - FootF(uR));
                J(p, "bone002", twist, -5 + 1.2 * Math.Sin(4 * Math.PI * u - 0.5), 0);
                // Head: steady, but not frozen. A soft bob a beat after each step, a slight sway, and
                // slow glances around.
                Head(p, -0.6 * twist + 6 * Math.Sin(t * 0.37) + 3 * Math.Sin(t * 0.91),
                    3 + 1.2 * Math.Sin(4 * Math.PI * u - 1.1) + 2 * Math.Sin(t * 0.29),
                    1.5 * Math.Sin(2 * Math.PI * u), -60 * WalkDir, 8, 0);
                break;
            }
            case "climb":
            {
                // Climbing straight up with her back to you. One hand reaches while the other pulls,
                // and the leg opposite the reaching hand steps up (opposite sides work together).
                double reachL = 0.5 + 0.5 * Math.Sin(climbPh), reachR = 1 - reachL; // 0 = pulled down, 1 = reaching up
                J(p, "bone026", 0, -105 - 50 * reachL, 30); J(p, "bone034", 85 - 60 * reachL, 0, 0);
                J(p, "bone032", 0, -105 - 50 * reachR, -30); J(p, "bone036", -(85 - 60 * reachR), 0, 0);
                double hipL = 8 + 55 * reachR, kneeL = 12 + 75 * reachR, hipR = 8 + 55 * reachL, kneeR = 12 + 75 * reachL;
                J(p, "bone003", 0, -hipL, 0); J(p, "bone005", 0, -hipR, 0);
                J(p, "bone007", 0, kneeL, 0); J(p, "bone008", 0, kneeR, 0);
                J(p, "bone021", 0, hipL - kneeL + 15, 0); J(p, "bone027", 0, hipR - kneeR + 15, 0);
                J(p, "bone012", 0, 0.58 * hipL, 0); J(p, "bone013", 0, 0.58 * hipL, 0);
                J(p, "bone016", 0, 0.58 * hipR, 0); J(p, "bone017", 0, 0.58 * hipR, 0);
                J(p, "bone002", 5 * Math.Sin(climbPh), -4, 0);
                // Eyes up at where she's going; noticing you, she looks back over her shoulder
                Head(p, 0, 14, 0, 70 * WalkDir, 4, 0);
                break;
            }
            case "hang":
            {
                // Dangling from your hand: shoulders hunched, arms and legs loose, feet kicking a little.
                // over: 0 = hanging upright, 1 = upside down. Loose limbs fall toward the ground, so as
                // she goes over, her arms end up past her head and her free leg folds.
                double kick = Math.Sin(t * 5.5), over = (1 - Math.Cos(sway * Math.PI / 180)) / 2;
                bool leftArm = grabJoint == "bone039", rightArm = grabJoint == "bone041";
                bool leftLeg = grabJoint == "bone007" || grabJoint == "bone021", rightLeg = grabJoint == "bone008" || grabJoint == "bone027";
                J(p, "bone002", 0, -6 + 10 * over, 0);
                // an arm she's held by is stretched straight up to your hand; the others hang
                J(p, "bone026", 0, leftArm ? -165 : -25 - 135 * over, 22); J(p, "bone034", leftArm ? 6 : 35 - 15 * over, 0, 0);
                J(p, "bone032", 0, rightArm ? -165 : -25 - 135 * over, -22); J(p, "bone036", -(rightArm ? 6 : 35 - 15 * over), 0, 0);
                // a leg she's held by is straight; the others kick, and fold when she's upside down
                J(p, "bone003", 0, leftLeg ? 0 : -14 - 45 * over - 8 * kick, 0); J(p, "bone007", 0, leftLeg ? 4 : 30 + 55 * over + 12 * kick, 0);
                J(p, "bone005", 0, rightLeg ? 0 : -14 - 45 * over + 8 * kick, 0); J(p, "bone008", 0, rightLeg ? 4 : 30 + 55 * over - 12 * kick, 0);
                J(p, "bone021", 0, leftLeg ? 0 : 30, 0); J(p, "bone027", 0, rightLeg ? 0 : 30, 0);
                Head(p, 0, -8 + 14 * over, 0, 0, 12, 0);
                break;
            }
            default: // stand
            {
                J(p, "bone026", 0, 0, 38 + br); J(p, "bone032", 0, 0, -38 - br);
                J(p, "bone034", 12, 0, 0); J(p, "bone036", -12, 0, 0);
                // nobody stands perfectly still: a slow, slight sway from side to side, the head keeping level
                J(p, "bone002", 0, 0, 1.3 * Math.Sin(t * 0.37) + 0.5 * Math.Sin(t * 0.91));
                // Impatient (restless): now and then a few slow taps of one foot, the toes lifting and
                // settling smoothly about once a second (faster looked frantic)
                if (Tapping && t % 20 < 3) J(p, "bone021", 0, -12 * (0.5 - 0.5 * Math.Cos(t * 2 * Math.PI * 1.0)), 0);
                // Noticing you: turns her head to you even if her body is still facing away
                double ly = Math.Max(-75, Math.Min(75, -0.85 * yawNow));
                Head(p, 8 * Math.Sin(t * 0.35), 2 * Math.Sin(t * 0.8), 2 * Math.Sin(t * 0.27), ly, 10, 4);
                break;
            }
        }
        if (Tune != null) ApplyTune(p, s);
        return p;
    }

    // Propped on her elbows with her forearms on the floor and her hands at the sides of the book
    // (palms in, thumbs up). These angles were solved so each arm segment points where the pose needs it.
    static readonly double[] ReadL = { 40, -110, 30, 70 }; // left upper arm (three angles) and elbow while reading (found by a search to mirror the right arm)
    void ReadingArms(BPose p, double br)
    {
        J(p, "bone002", 0, 6 + 1.2 * br, 0); // (a gentle curve: 18 arched her back uncomfortably)
        J(p, "bone026", ReadL[0], ReadL[1], ReadL[2]); J(p, "bone034", ReadL[3], 0, 0); J(p, "bone039", -9, 19, 0);
        J(p, "bone032", -91, -102, -63); J(p, "bone036", -88, 0, 0); J(p, "bone041", -17, -2, 0);
    }

    // Nodding off while sitting: how far her head has drooped (0.45 to 1). It sinks slowly for 16
    // seconds, then she catches herself and it snaps back up in under half a second.
    static double NodOff(double t)
    {
        double u = (t % 17) / 17;
        return u < 0.975 ? 0.45 + 0.55 * Ease(u / 0.975) : 1 - 0.55 * Ease((u - 0.975) / 0.025);
    }

    // Arm angles (upper arm's three, then the elbow), found by a search for hands resting where they
    // belong (her lap, her knees, the floor) without sinking into her thighs, shins or body
    static readonly double[] SitL = { -16, -19, 66, 37 }, SitR = { -15, -4, -43, -46 };
    static readonly double[] CrouchL = { 5, -39, 45, 53 }, CrouchR = { -10, -24, -45, -30 };
    static readonly double[] FootFwdR = { -76, -37, -9, 26 };
    static readonly double[] HalfKneelR = { -9, -16, -12, -87 }; // her hand resting on her front knee, not sinking into it

    // Arms folded on the floor in front of her face, forearms crossed, to rest her head on (k: how far into it)
    void FoldedArms(BPose p, double k)
    {
        if (k <= 0) return;
        Toward(p, "bone026", FoldL[0], FoldL[1], FoldL[2], k); Toward(p, "bone034", FoldL[3], 0, 0, k); Toward(p, "bone039", 0, 0, 0, k);
        Toward(p, "bone032", FoldR[0], FoldR[1], FoldR[2], k); Toward(p, "bone036", FoldR[3], 0, 0, k); Toward(p, "bone041", 0, 0, 0, k);
    }
    // upper arm (three angles) and elbow bend, left and right
    // (found by a search that scored wrists and elbows level with her hips, close together, in front of her face)
    static readonly double[] FoldL = { 50, -100, 60, 130 }, FoldR = { -55, -94, -82, -123 };

    // Lying on her stomach, propped up on her elbows: her body slopes up from her hips (on the
    // floor) to her shoulders (an upper arm's length above it), the way a real person lies. Without
    // the slope her elbows were the lowest thing and her hips and legs hovered over the floor.
    // (slump: 0 = propped up, 1 = lying flat with her head down on her folded arms, asleep)
    const double Prop = 12;
    void Propped(BPose p, double slump = 0)
    {
        double prop = Prop * (1 - slump);
        p.Pitch = 90 - prop; p.Sink = 25;
        J(p, "bone003", 0, prop, 0); J(p, "bone005", 0, prop, 0); // legs straightened back to lie along the floor
    }

    // ...and her head tipped back down by the same amount, so she still looks at her book (not when looking at you)
    void PropHead(BPose p, double look) { Add(p, "bone006", 0, -0.8 * Prop * (1 - look), 0); }

    // Eyes on the book, slowly following the lines and dipping a little at the end of each one;
    // noticing you, she lifts her head and turns to look at you (with a small twist of the shoulders).
    // (awake: 1 = reading, 0 = dozing, when her eyes stop following the lines)
    void ReadingHead(BPose p, double t, double L, double awake = 1)
    {
        double scan = (4 * Math.Sin(t * 0.8) + 1.5 * Math.Sin(t * 2.3)) * awake;
        double dip = 1.5 * Math.Sin(t * 0.5) * awake;
        double[] neckRead = { 2 + scan * 0.4, 17 + dip, -2 }, headRead = { -5 + scan * 0.6, 6, 0 };
        double[] neckLook = side > 0 ? new double[] { -23, 21, 14 } : new double[] { 23, 26, -4 };
        double[] headLook = side > 0 ? new double[] { -26, 16, -5 } : new double[] { 28, 14, -16 };
        double twist = (side > 0 ? -25 : 12) * L;
        J(p, "bone004", twist, 10, 0);
        J(p, "bone006", neckRead[0] + (neckLook[0] - neckRead[0]) * L, neckRead[1] + (neckLook[1] - neckRead[1]) * L, neckRead[2] + (neckLook[2] - neckRead[2]) * L);
        J(p, "bone009", headRead[0] + (headLook[0] - headRead[0]) * L, headRead[1] + (headLook[1] - headRead[1]) * L, headRead[2] + (headLook[2] - headRead[2]) * L);
    }

    // A person never moves every joint at once. Starting a move from rest, her legs and hips go first,
    // her body a moment later, then her arms, and her hands and head last (the "overlapping action"
    // animators talk about); everything arrives together. (Not when a move flows on from the one before:
    // she's already moving, and holding a joint back there would look like a hitch.)
    static readonly Dictionary<string, double> Lag = new Dictionary<string, double>
    {
        { "bone002", 0.04 }, { "bone004", 0.06 }, { "bone006", 0.1 }, { "bone009", 0.14 },
        { "bone026", 0.1 }, { "bone032", 0.1 }, { "bone034", 0.14 }, { "bone036", 0.14 }, { "bone039", 0.18 }, { "bone041", 0.18 },
        { "bone012", 0.08 }, { "bone013", 0.08 }, { "bone016", 0.08 }, { "bone017", 0.08 }
    };

    static void Stagger(BPose p, BPose a, BPose b, double k, bool flowsOn)
    {
        foreach (var kv in Lag)
        {
            double[] to;
            if (!b.J.TryGetValue(kv.Key, out to)) continue;
            double[] fr;
            if (!a.J.TryGetValue(kv.Key, out fr)) fr = new double[3];
            double kj = Math.Max(0, Math.Min(1, (k - kv.Value) / (1 - kv.Value)));
            double e = flowsOn ? kj * kj * (2 - kj) : Ease(kj);
            p.J[kv.Key] = new[] { fr[0] + (to[0] - fr[0]) * e, fr[1] + (to[1] - fr[1]) * e, fr[2] + (to[2] - fr[2]) * e };
        }
    }

    // Extra bend through the middle of some moves, so kneeling down dips through a half squat
    static void Arc(BPose p, string a, string b, double w)
    {
        if (w <= 0) return;
        if (Pair(a, b, "stand", "halfkneel"))
        {
            // Standing up off the front leg she leans her body forward over it, the way you shift your
            // weight onto a foot before you push up from it. (Tipping her forward tips her legs too, so
            // her hips turn back by the same amount: her legs stay where they were and only her body leans.)
            p.Pitch += 20 * w;
            Add(p, "bone003", 0, -20 * w, 0); Add(p, "bone005", 0, -20 * w, 0); // (- bends the hip forward)
        }
        if (Pair(a, b, "stand", "crouch") || Pair(a, b, "crouch", "sit") || Pair(a, b, "allfours", "footfwd") || Pair(a, b, "footfwd", "halfkneel") || Pair(a, b, "halfkneel", "stand"))
        {
            // On the way from one place to the next, her arms swing out round her legs instead of straight
            // through them (blending the angles alone would move a hand through a thigh)
            Add(p, "bone026", SwingOut[0] * w, SwingOut[1] * w, 0); Add(p, "bone032", SwingOut[2] * w, SwingOut[3] * w, 0);
        }
        if (Pair(a, b, "crouch", "sit") || Pair(a, b, "stand", "crouch"))
        {
            // her hands move from her knees to her lap over her thighs, lifted, not through them
            Add(p, "bone034", 55 * w, 0, 0); Add(p, "bone036", -45 * w, 0, 0); // (her left hand rests on the thigh that lifts, so it lifts more)
        }
        if (Pair(a, b, "footfwd", "halfkneel") || Pair(a, b, "halfkneel", "stand") || Pair(a, b, "allfours", "footfwd"))
        {
            // her right hand goes from the floor up to her knee round the outside of her leg, lifted clear
            Add(p, "bone036", -50 * w, 0, 0); Add(p, "bone032", -15 * w, 0, 0);
        }
        if (Pair(a, b, "lie", "allfours"))
        {
            // Pushing back from her elbows onto hands and knees, her elbows stay bent until her knees are
            // under her, so her body stays low instead of rising on straight arms
            Add(p, "bone034", 45 * w, 0, 0); Add(p, "bone036", -45 * w, 0, 0);
        }
    }

    // How far each arm swings out round her legs mid-move (left x, y, right x, y), found by a search that
    // played getting up, lying down, sitting down and standing up and counted points passing through her
    static readonly double[] SwingOut = { -30, -37, -57, -40 };

    static void ApplyTune(BPose p, string s)
    {
        foreach (var kv in Tune)
        {
            if (!kv.Key.StartsWith(s + ".")) continue;
            string k = kv.Key.Substring(s.Length + 1);
            if (k == "pitch") p.Pitch = kv.Value[0];
            else if (k == "sink") p.Sink = kv.Value[0];
            else p.J[k] = (double[])kv.Value.Clone();
        }
    }
}

// Heather hanging out on the taskbar. Every so often she gets up, walks somewhere else and then
// lies down, sits on the edge or just stands there. Hover to get her attention, click her to open the
// quiz, drag her anywhere (up off the taskbar too, and across monitors), right-click for her menu.
class BuddyWindow : Window
{
    // GroundY: where the taskbar's top edge falls in the window. The window is roomy (the empty part
    // is see-through and lets clicks through) so she's never cut off: swung by a hand she reaches
    // about 375 to either side of her middle and 20 above the window's old top, and sitting on an
    // edge her boots hang 130 below her floor. The self-test measures this.
    const double W = 880, H = 730, GroundY = 540, WorldPerDip = 2.3;
    readonly HeatherView view;
    readonly Action onClick;
    readonly Stats stats;
    readonly Border bubble;
    readonly TextBlock bubbleText;
    readonly DispatcherTimer watch;
    bool walking;
    readonly Random rng = new Random();
    Point downAt;
    double downLeft, downTop, walkTarget;
    Point carryAt, downIn; // downIn: where on her the mouse went down
    double carryTime;
    Vector carryV; // how fast she's being carried, in units a second
    readonly Stopwatch carryClock = Stopwatch.StartNew();
    string afterWalk = "sit";
    bool dragging, fullscreenApp, pausedWalk, wasSnoozed;
    DateTime nextChange, snoozeUntil = DateTime.MinValue;
    public bool Suppressed, Disabled;
    // Told when she goes into hiding (with how long, in words) so the app can say how to bring her back
    public Action<string> Snoozed;

    public bool IsSnoozed { get { return DateTime.Now < snoozeUntil; } }

    // ---------- Lines ----------
    // What she says, by situation ("hover") and, where her mood changes it, by mood ("hover.sleepy").
    // Her voice: short, dry, a little bored. Six words at most (the self-test counts).
    static readonly Dictionary<string, string[]> Pool = new Dictionary<string, string[]>
    {
        { "hover", new[] { "Study time?", "Click me. I'm bored.", "Quiz?", "I'm waiting...", "Let's learn something.", "Oh. Hey.", "What.",
            "You again.", "Yes?", "Need something?", "Still here.", "Go on. Click.", "Five questions. Come on.", "I don't bite.", "Staring is rude." } },
        { "hover.sleepy", new[] { "It's late.", "Go to bed.", "Why are you up?", "One quiz. Then sleep.", "I was almost asleep.", "Do you ever sleep?" } },
        { "hover.restless", new[] { "Remember me?", "It's been a while.", "Your notes miss you.", "Still avoiding it?", "The quiz won't bite.",
            "Forgot how to click?", "I'm collecting dust." } },
        { "hover.pleased", new[] { "Back already?", "You did okay earlier.", "Take your break.", "Another round?", "Don't burn out.", "Not bad today.",
            "Fine. That was decent." } },
        { "hover.bored", new[] { "I'm so bored.", "Entertain me.", "Nothing to do down here.", "Quiz me. Or yourself.", "Anything. Please.", "Counting pixels. Help." } },
        { "idle", new[] { "Hm.", "...", "Slow day.", "Quiet down here.", "Whatever.", "Huh." } },
        { "idle.sleepy", new[] { "*yawn*", "So tired.", "Five more minutes.", "Mm. Late.", "Can't keep my eyes open." } },
        { "idle.restless", new[] { "Any day now.", "Still waiting.", "I could be teaching.", "Tick tock.", "Bored. So bored." } },
        { "idle.pleased", new[] { "Good session.", "This is fine.", "Peaceful.", "Could be worse.", "Not a bad day." } },
        { "idle.bored", new[] { "Bored.", "Nothing ever happens.", "Is it Friday yet?", "Same taskbar. Every day.", "Sigh." } },
        { "drag", new[] { "Hey.", "Put me down.", "Seriously?", "I can walk, you know.", "Careful.", "Whoa.", "Ask first.", "Not the hair." } },
        { "drag.sleepy", new[] { "I was comfortable.", "Not now...", "Let me sleep." } },
        { "drop", new[] { "Fine. Here, then.", "New spot. Great.", "Thanks. I guess.", "Was that necessary?", "Okay. Here works.", "Happy now?", "Warn me next time." } },
        { "back", new[] { "I'm back. Miss me?", "I'm back.", "Miss me?", "That was a nap.", "Did you study?", "Right where I left off.", "Okay. Break's over." } },
        { "return", new[] { "Oh. You're back.", "Finally.", "Where were you?", "Took you long enough.", "I kept your spot.", "Welcome back, I guess.", "There you are." } },
        { "return.sleepy", new[] { "You should be asleep.", "Back? At this hour?", "Still up. Of course." } },
        { "return.restless", new[] { "Back. Quiz now?", "Good. Now study.", "You owe me a quiz." } },
        { "return.pleased", new[] { "Break's over already?", "Round two?" } },
        { "perch", new[] { "Nice view.", "Mine now.", "Better up here.", "Don't close this one.", "I can see everything." } },
        { "fall", new[] { "Hey. My seat.", "Rude.", "I was sitting there.", "A little warning?" } },
        { "land", new[] { "Ow.", "Nice throw.", "Stuck the landing.", "Do not do that.", "Again? No." } },
        { "pet", new[] { "Stop that.", "I'm not a cat.", "...Fine. Continue.", "Hands off the hair.", "Okay, okay." } },
        { "doze", new[] { "zz...", "z z z", "...mm. zz", "zzz", "...five more pages. zz" } },
        { "woken", new[] { "Mm? I'm up.", "Wasn't asleep.", "...what time is it?", "Just resting my eyes.", "Five more minutes." } },
        { "dizzy", new[] { "Room's spinning.", "Whoa. Two of you.", "Never again.", "...ugh. Dizzy.", "Which way is up?" } },
        { "ride", new[] { "Whoa.", "Hey. Easy.", "Warn me first.", "I'm sitting here!", "Bumpy." } },
        { "morning", new[] { "Morning.", "Is it morning already?", "Up. Mostly.", "...okay. Awake.", "New day. Quiz later?" } },
        { "habit", new[] { "Usually about now.", "Your usual quiz hour.", "This is when you study.", "Same time as always?", "Around now, usually. Quiz?" } },
        { "break", new[] { "Fifty minutes. Stretch.", "Break time. Stand up.", "Drink some water.", "Look away from the screen.", "Rest your eyes a minute." } },
        { "aftergood", new[] { "Okay. That was good.", "Fine. I'm impressed.", "See? You know this.", "Good run." } },
        { "afterbad", new[] { "Rough one. Again tomorrow.", "We'll get those next time.", "That hurt to watch.", "Okay. Review time." } }
    };
    string lastLine;

    // Lines made from your numbers: what the Stats class tracks, and how long it's been
    static List<string> StatLines(DateTime now, Stats s)
    {
        var lines = new List<string>();
        if (s.Answered == 0) lines.Add("Never tried a quiz, huh.");
        if (s.Answered >= 10)
        {
            int acc = Accuracy(s);
            lines.Add(acc + (acc >= 85 ? "% right. Show-off." : acc < 60 ? "% right. We'll fix it." : "% right. Not terrible."));
        }
        if (s.BestStreak >= 5) lines.Add("Best streak: " + s.BestStreak + ". Beat it.");
        if (s.Xp > 0) lines.Add(s.Xp + " XP. Keep going.");
        if (s.LastQuiz != DateTime.MinValue && (now - s.LastQuiz).TotalDays >= 2) lines.Add((int)(now - s.LastQuiz).TotalDays + " days. Really?");
        // "Bio 1 - General Biology" -> "More Bio 1?"
        string cls = s.LastClass.Split(new[] { " - " }, StringSplitOptions.None)[0].Trim();
        if (cls.Length > 0 && cls.Length <= 12) lines.Add("More " + cls + "?");
        string exam = ExamLine(now, s);
        if (exam != null) lines.Add(exam);
        if (now.Hour >= 6 && now.Hour < 10) lines.Add("Morning. Coffee first?");
        return lines;
    }

    // A line for the situation. More often than not it comes from her mood's own pool, when the
    // situation has one; hovering sometimes gets a line about your numbers instead. Never the same
    // line twice in a row.
    string Line(string context, string mood)
    {
        var pool = Pool[context].ToList();
        var stat = context == "hover" ? StatLines(DateTime.Now, stats) : new List<string>();
        string[] moody;
        double r = rng.NextDouble();
        if (r < 0.6 && Pool.TryGetValue(context + "." + mood, out moody)) pool = moody.ToList();
        else if (r > 0.6 && context == "hover" && ExamDays(DateTime.Now, stats) <= 14) pool = new List<string> { ExamLine(DateTime.Now, stats) }; // an exam two weeks off or less comes up a lot
        else if (r > 0.8 && stat.Count > 0) pool = stat;
        pool.Remove(lastLine);
        if (pool.Count == 0) pool = Pool[context].Where(l => l != lastLine).ToList();
        return lastLine = pool[rng.Next(pool.Count)];
    }

    // ---------- Missed questions ----------
    // The quiz tells her how many questions a review of what you got wrong would have (Missed), and
    // how to start one (Review). About half the time you hover she offers it, and while that offer
    // is showing, a click starts the review instead of just opening the quiz.
    public Func<int> Missed;
    public Action Review;
    bool offering;

    string HoverLine()
    {
        int n = Missed != null ? Missed() : 0;
        offering = n > 0 && rng.Next(2) == 0;
        if (!offering) return Line("hover", Mood);
        var offers = new List<string> { "Redo " + n + " you missed?", n + " to fix. Click me.", "Got " + n + " wrong before. Again?" };
        offers.Remove(lastLine);
        return lastLine = offers[rng.Next(offers.Count)];
    }

    void Clicked()
    {
        if (offering && Review != null) Review(); else onClick();
    }

    // From the right-click menu, which is always there even when she isn't offering
    void ReviewFromMenu()
    {
        if (Review != null && Missed != null && Missed() > 0) Review();
        else Say("Nothing to redo. Huh.");
    }

    // Her answer to something you did: a line, and the first of these moments that suits her pose
    void Respond(string context, params string[] moments)
    {
        Say(Line(context, Mood));
        string pose = view.Settled;
        Act(moments.FirstOrDefault(m => HeatherView.Fits(m, pose)));
        nextMoment = DateTime.Now.AddSeconds(25 + rng.Next(20)); // then leave her be for a bit
    }

    // Back on the taskbar after hiding. Brought back by you: a little "ta-da". On her own: a stretch.
    void ComeBack(bool asked)
    {
        if (asked) Respond("back", "cheer", "stretch"); else Respond("back", "stretch");
    }

    // ---------- You leaving and coming back ----------
    const double AwaySeconds = 300; // keyboard and mouse untouched this long = you're away
    bool away;

    // Called once a second with how long the keyboard and mouse have been untouched.
    // True on the tick where you've just come back.
    bool BackFromAway(double idleSeconds)
    {
        if (idleSeconds >= AwaySeconds) { away = true; return false; }
        if (!away) return false;
        away = false;
        return true;
    }

    // She looks over at wherever the mouse is and says something. If it's been days since a quiz,
    // she points at you; otherwise a small nod.
    void WelcomeBack(string mood)
    {
        Watch(DateTime.Now, 5);
        if (mood == "restless") Respond("return", "point");
        else Respond("return", "wave", "glance"); // (lying down she can't wave: she looks up instead)
    }

    public BuddyWindow(HeatherModel model, Stats stats, Action onClick)
    {
        this.stats = stats;
        double x = stats.BuddyX;
        this.onClick = onClick;
        Title = "Heather";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = W;
        Height = H;
        var wa = SystemParameters.WorkArea;
        Top = wa.Bottom - GroundY; // a first guess, so the lines below know roughly how high she is
        Left = double.IsNaN(x) ? wa.Right - W - 150 : x;
        shelfY = stats.BuddyY; // back on the shelf you left her on, if there's still a monitor there
        CheckShelf();
        Left = Clamp(Left);
        Ground();

        var root = new Grid { Background = Brushes.Transparent };
        view = new HeatherView(model, false, W, H, new Point3D(0, 5, 0), WorldPerDip, new Vector3D(0.1, 1, -0.3), GroundY, true);
        view.Fps = 144; // a ceiling well above what WPF offers, so she draws every frame she can; 24 while you're away (see the watch timer)
        // Mouse tests go against a flat, see-through sheet over her instead of her 3D body. Windows only
        // sends the mouse here where a pixel of her is actually drawn, so clicks still land on her
        // and pass through the empty space; but each mouse move no longer has to be checked against
        // every triangle of the model, which was costing frames.
        view.Viewport.IsHitTestVisible = false;
        view.Background = Brushes.Transparent;
        view.NextSide = SideAt(Left, rng.Next(2) == 0 ? 1 : -1);
        view.SnapTo(rng.Next(2) == 0 ? "lie" : "sit");
        // After she gets up, the window takes over any sideways offset so she never slides
        view.ShiftWindow = d => { if (!dragging) Left = Clamp(Left + d); };
        view.Cursor = Cursors.Hand;
        view.ToolTip = "Click to study · drag her anywhere · rub the mouse over her to pet · right-click for more";
        view.ContextMenu = BuildMenu();
        root.Children.Add(view);

        bubble = new Border
        {
            CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x11, 0x10)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 6), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false
        };
        bubbleText = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xE3, 0xD6)),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI")
        };
        bubble.Child = bubbleText;
        root.Children.Add(bubble);
        Content = root;

        // Hovering: she notices you. If she's walking she stops; she turns her head and looks up at you
        // (or back over her shoulder when she's lying down). No bouncing.
        view.MouseEnter += (s, e) => Hovered(DateTime.Now);
        view.MouseLeave += (s, e) =>
        {
            view.Looking = false;
            offering = false;
            leftAt = DateTime.Now;
            // a hover line goes when the mouse does; something she said (like a reaction to petting) stays its full time
            if (!Speaking(leftAt)) bubble.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            if (pausedWalk && !dragging)
            {
                pausedWalk = false;
                view.Activity = "walk";
            }
        };
        view.MouseLeftButtonDown += (s, e) =>
        {
            downAt = MouseOnScreen();
            downLeft = Left;
            downTop = Top;
            downIn = e.GetPosition(view);
            dragging = false;
            view.CaptureMouse();
        };
        view.MouseMove += (s, e) =>
        {
            if (!view.IsMouseCaptured) return; // (petting is followed every frame, below)
            // Where the pointer really is on the screen, asked of Windows directly. (The position
            // that comes with this event is measured from her window, and her window is itself
            // being moved to follow the pointer; measuring from it made her own swinging look like
            // you flinging the mouse, and that flung her.)
            var p = MouseOnScreen();
            if ((Math.Abs(p.X - downAt.X) > 4 || Math.Abs(p.Y - downAt.Y) > 4) && !dragging)
            {
                PickUp(downIn);
                carryAt = p;
                carryTime = carryClock.Elapsed.TotalSeconds;
            }
            if (dragging) Carried(p, carryClock.Elapsed.TotalSeconds);
        };
        view.MouseLeftButtonUp += (s, e) =>
        {
            view.ReleaseMouseCapture();
            // (a mouse that stopped before the button came up isn't moving any more, however fast it was)
            if (dragging) LetGo(carryClock.Elapsed.TotalSeconds - carryTime < 0.08 ? carryV : new Vector());
            else Clicked();
        };

        SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };

        // Walking moves the window in step with each animation frame (smooth, and in time with her feet)
        // ...and while she's watching the mouse, the same frames keep her head aimed at it (no extra timer)
        // ...and a showing bubble stays over her head. (This used to run on every monitor refresh, 360
        // times a second, re-measuring the bubble each time; that alone was costing her frames.)
        view.Stepped = dt =>
        {
            WalkStep(dt);
            ClimbStep(dt);
            FallStep(dt);
            if (landing) Land();
            RidePerch(dt);
            if (dragging)
            {
                view.SwayTarget *= Math.Exp(-dt * 5); // held still, she stops swinging
                // ...and as she swings and kicks, the part you're holding stays in your hand
                PinGrab(MouseOnScreen());
            }
            if (view.Watching) AimGaze(MouseFromHead());
            if (bubble.Opacity > 0.01) FollowBubble();
            // Petting: while the mouse is over her, and for a moment after it leaves (a rub across her
            // turns round just past her edge, where her window can't see the mouse), follow the pointer
            if (!dragging && (view.IsMouseOver || (DateTime.Now - leftAt).TotalSeconds < 0.6)) Petting(MouseOnScreen(), DateTime.Now);
        };


        // Stay above the taskbar, step out of the way of fullscreen apps, and now and then do something new
        fullscreenApp = Native.ForegroundIsFullscreen(); // know before she's first shown, so she never flashes over a game
        nextChange = DateTime.Now.AddSeconds(20 + rng.Next(25));
        nextMoment = DateTime.Now.AddSeconds(8 + rng.Next(8));
        watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watch.Tick += (s, e) =>
        {
            fullscreenApp = Native.ForegroundIsFullscreen();
            bool snoozed = IsSnoozed;
            bool returned = BackFromAway(Native.IdleSeconds());
            view.Fps = away ? 24 : 144; // nobody's watching: no need to draw her smoothly
            Refresh();
            if (wasSnoozed && !snoozed && IsVisible) ComeBack(false);
            wasSnoozed = snoozed;
            if (!IsVisible) return;
            if (returned) activeSince = DateTime.Now;
            if (returned && !dragging) WelcomeBack(Mood);
            // (a debugging aid: while this file exists, she notes how many frames she really drew each second)
            try
            {
                if (File.Exists(FpsLog))
                {
                    // frames she drew, chances WPF gave her to draw, what she was doing, and how WPF is drawing (tier 2 = on the graphics card)
                    var source = PresentationSource.FromVisual(this) as HwndSource;
                    File.AppendAllText(FpsLog, DateTime.Now.ToString("HH:mm:ss") + "  " + (view.Frames - lastFrames) + " fps  " + (view.Ticks - lastTicks) + " ticks  "
                        + (view.Settled ?? "moving") + "  tier " + (RenderCapability.Tier >> 16) + " " + (source != null ? source.CompositionTarget.RenderMode.ToString() : "?") + "\r\n");
                }
            }
            catch { }
            lastFrames = view.Frames;
            lastTicks = view.Ticks;
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            // Keep her on a floor that exists (a monitor can be unplugged or rearranged under her)
            if (!dragging && !falling && !landing)
            {
                CheckPerch();
                CheckShelf();
                if (!walking) Left = Clamp(Left);
                Ground();
            }
            string mood = Mood;
            view.Energy = MoodEnergy(mood);
            view.Tapping = mood == "restless";
            bool morning = MorningDue(mood);
            view.Dozing = ShouldDoze(DateTime.Now, mood);
            if (morning && !away) Respond("morning", "stretch"); // (she's waking up: the stretch plays as she picks her book back up)
            if (!view.Dozing) NoticeTick(MouseFromHead(), DateTime.Now); // asleep, she doesn't notice the mouse nearby (hovering wakes her)
            string settled = view.Settled;
            bool free = !dragging && !falling && !landing && !view.Looking && !view.Watching && !pausedWalk && climbing == 0 && settled != null && settled != "walk";
            if (free && BreakDue(DateTime.Now, Native.IdleSeconds()) && !view.Dozing) Respond("break", "stretch");
            if (free && !away && NudgeDue(DateTime.Now)) Nudge(DateTime.Now);
            if (free && DateTime.Now >= nextChange) DoSomething();
            else if (free && !away && DateTime.Now >= nextMoment) IdleMoment(settled); // no acting for an empty room
        };
        watch.Start();
    }

    // ---------- Being picked up, put down and thrown ----------
    const double SwayPerSpeed = 0.022; // degrees of tilt per unit-a-second she's carried sideways
    const double ThrowSpeed = 500;     // let go while moving faster than this (units a second) and it's a throw
    const double Gravity = 2600;       // units a second, every second
    bool falling;
    Vector fallV;

    // at: where on her the mouse took hold (null = nowhere in particular)
    void PickUp(Point? at)
    {
        dragging = true;
        StopWalking();
        falling = false;
        landing = false;
        carryV = new Vector();
        view.Grab(at);
        view.Held = true; // she goes limp and dangles from the part you've got hold of
        Say(Line("drag", Mood));
    }

    // Where the pointer is on the whole screen, in WPF units
    static Point MouseOnScreen()
    {
        var c = System.Windows.Forms.Cursor.Position;
        return Dip(new Rect(c.X, c.Y, 0, 0)).Location;
    }

    // The pointer moved to here (a screen position) at this time (seconds) while carrying her.
    // Works out how fast it's going, smoothed: that swings her, and decides whether letting go is a throw.
    void Carried(Point at, double time)
    {
        double gap = time - carryTime;
        if (gap > 0.004)
        {
            carryV = carryV * 0.6 + new Vector((at.X - carryAt.X) / gap, (at.Y - carryAt.Y) / gap) * 0.4;
            carryAt = at;
            carryTime = time;
            view.SwayTarget = Math.Max(-28, Math.Min(28, carryV.X * SwayPerSpeed));
        }
        PinGrab(at);
    }

    // Move her window so the part of her you're holding sits at this spot on the screen
    void PinGrab(Point screen)
    {
        var held = view.GrabPoint(); // where that part is inside her window
        if (double.IsNaN(held.X) || double.IsNaN(held.Y)) return;
        Left = screen.X - held.X;
        Top = screen.Y - held.Y;
        holdAt = screen;
    }

    Point holdAt; // where on the screen the held part of her is
    bool landing; // just let go of: righting herself around that spot

    // Every frame after a gentle let-go, or after touching down from a fall: she swings back
    // upright around the part that's being held still (the part you were holding, or her chest
    // after a fall). Once she's upright she's standing there.
    void Land()
    {
        PinGrab(holdAt);
        if (view.HeldAmount > 0.02) return;
        landing = false;
        view.Grabbed = "bone009";
        double was = Left;
        PutDown();
        nextChange = DateTime.Now.AddSeconds(6 + rng.Next(6)); // she settles back down soon after being moved
        // Left partly off the edge of the screens, PutDown pulled her window back on in one jump.
        // Instead she stays where you left her and walks back on.
        if (Math.Abs(Left - was) > 2 && view.Activity != "sit")
        {
            double inside = Left;
            Left = was;
            Ground();
            WalkTo(inside, "stand");
            fell = false;
            return;
        }
        if (!fell) { Respond("drop", "shift", "glance"); return; }
        fell = false;
        // thrown hard, she lands dizzy: her head wobbles round in circles for a couple of seconds
        if (hardThrow) { hardThrow = false; Act("dizzy"); Say(Line("dizzy", Mood)); return; }
        view.Nod();
        if (rng.Next(2) == 0) Say(Line("land", Mood));
    }

    bool hardThrow; // the fall in progress started with a hard throw

    // speed: how fast the mouse was moving when the button came up
    void LetGo(Vector speed)
    {
        dragging = false;
        if (speed.Length > ThrowSpeed)
        {
            StartFall(speed.X * 0.7, speed.Y * 0.7); // nothing's holding her any more: she tumbles upright in the air
            return;
        }
        view.Held = false;
        view.SwayTarget = 0;
        fell = false;
        landing = true; // (Land finishes putting her down once she's upright)
    }

    // She's in the air with this much speed (x to the right, y downward): thrown, or her window just closed
    void StartFall(double vx, double vy)
    {
        StopWalking();
        perch = IntPtr.Zero;
        if (double.IsNaN(shelfY)) shelfY = Top + GroundY;
        // From here on it's her middle (her chest) that travels: it starts exactly where it is on
        // screen now, however she was being held, so nothing jumps as she lets go and rights herself
        view.Grabbed = "bone006";
        var chest = view.GrabPoint();
        fallAt = new Point(Left + chest.X, Top + chest.Y);
        fallV = new Vector(vx, vy);
        hardThrow = fallV.Length > 1000;
        falling = true;
        landing = false;
        view.Held = true;
    }

    Point fallAt; // where her chest is on the screen while she's in the air

    // Every frame while she's in the air: gravity pulls her chest along its path until her feet
    // (where they'll be once she's upright: ChestHigh below her chest) reach a window's top edge
    // or the taskbar. The sides and top of the screen are walls she bounces off.
    void FallStep(double dt)
    {
        if (!falling) return;
        dt = Math.Min(dt, 0.05);
        double feetWas = fallAt.Y + view.ChestHigh, l, r;
        fallV.Y += Gravity * dt;
        fallAt += fallV * dt;
        var m = MonitorAt(fallAt.X, fallAt.Y);
        Span(fallAt.X, double.NaN, out l, out r);
        if (fallAt.X < l + 60 || fallAt.X > r - 60) { fallAt.X = Math.Max(l + 60, Math.Min(r - 60, fallAt.X)); fallV.X *= -0.4; }
        if (fallAt.Y < m.Top + MinFloor) { fallAt.Y = m.Top + MinFloor; fallV.Y = Math.Abs(fallV.Y) * 0.3; }
        view.SwayTarget = Math.Max(-28, Math.Min(28, fallV.X * SwayPerSpeed));
        double feet = fallAt.Y + view.ChestHigh;
        if (fallV.Y > 0)
        {
            // Coming down through the top edge of a window: that's where she lands
            var edge = EdgeAt(fallAt.X, feet, Math.Max(8, feet - feetWas));
            if (edge.HasValue && edge.Value.Value.Top > feetWas + 0.5) { FeetDown(edge.Value.Value.Top); return; }
        }
        if (feet >= m.Bottom) { FeetDown(m.Bottom); return; }
        shelfY = feet;
        PinGrab(fallAt);
    }

    // Her feet have reached a floor at this height. She stops there; Land rights her around her
    // chest and then settles her onto whatever that floor is (the taskbar, or the window she hit).
    void FeetDown(double floor)
    {
        fallAt.Y = floor - view.ChestHigh;
        falling = false;
        view.SnapTo("stand"); // she lands on her feet, whatever she was doing before she fell
        view.Held = false;
        view.SwayTarget = 0;
        PinGrab(fallAt);
        landing = true;
        fell = true;
    }

    bool fell; // the landing in progress is the end of a fall or a throw, not a gentle put-down

    // ---------- Petting ----------
    // Rub the mouse back and forth over her (side to side or up and down) and she shrugs you off:
    // a hand goes to her head, she shakes it, and she says something.
    Point rubAt;
    int rubDirX, rubDirY, rubs;
    DateTime lastRub, nextPet;

    void Petting(Point at, DateTime now)
    {
        // each time the mouse turns round on either axis, after moving a little, counts as one rub
        bool turned = false;
        if (Math.Abs(at.X - rubAt.X) >= 6)
        {
            int dir = Math.Sign(at.X - rubAt.X);
            if (dir != rubDirX) { rubDirX = dir; turned = true; }
            rubAt.X = at.X;
        }
        if (Math.Abs(at.Y - rubAt.Y) >= 6)
        {
            int dir = Math.Sign(at.Y - rubAt.Y);
            if (dir != rubDirY) { rubDirY = dir; turned = true; }
            rubAt.Y = at.Y;
        }
        if (!turned) return;
        rubs = (now - lastRub).TotalSeconds > 1.5 ? 1 : rubs + 1;
        lastRub = now;
        if (rubs < 4 || now < nextPet) return;
        rubs = 0;
        nextPet = now.AddSeconds(12);
        Respond("pet", "facepalm", "glance");
        view.Shake();
    }

    // ---------- Break reminder ----------
    // Fifty minutes at the keyboard without a real pause and she tells you to take one
    DateTime activeSince = DateTime.Now;

    bool BreakDue(DateTime now, double idleSeconds)
    {
        if (idleSeconds > 120) activeSince = now; // two minutes away from the keys counts as a pause
        if ((now - activeSince).TotalMinutes < 50) return false;
        activeSince = now;
        return true;
    }

    // ---------- Exam countdown ----------
    // Days until the exam you told her about (null if there isn't one, or it's past)
    static int? ExamDays(DateTime now, Stats s)
    {
        if (s.ExamDate == DateTime.MinValue) return null;
        int days = (s.ExamDate.Date - now.Date).Days;
        return days < 0 ? (int?)null : days;
    }

    static string ExamLine(DateTime now, Stats s)
    {
        var days = ExamDays(now, s);
        if (!days.HasValue) return null;
        string name = string.IsNullOrWhiteSpace(s.ExamName) ? "Exam" : s.ExamName.Trim();
        return days == 0 ? name + " is today. Go." : days == 1 ? name + " is tomorrow." : name + " in " + days + " days.";
    }

    void SetExam(string name, DateTime date)
    {
        stats.ExamName = string.IsNullOrWhiteSpace(name) ? "Exam" : name.Trim();
        stats.ExamDate = date;
        stats.Save();
        Say(ExamLine(DateTime.Now, stats) ?? "No exam. Lucky you.");
    }

    // From the right-click menu
    void AskExamDate() { ExamWindow().Show(); }

    // A small window, in the same dark red-and-cream look as her menu and speech bubble, to name the
    // exam and pick its date. The date is picked with steppers (a day or a week at a time) instead
    // of a calendar, because WPF's calendar only comes in light grey.
    Window ExamWindow()
    {
        var cream = new SolidColorBrush(Color.FromRgb(0xEA, 0xE3, 0xD6));
        var muted = new SolidColorBrush(Color.FromArgb(0xA0, 0xEA, 0xE3, 0xD6));
        var red = new SolidColorBrush(Color.FromRgb(0xC2, 0x3B, 0x30));
        var faint = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        DateTime date = stats.ExamDate.Date >= DateTime.Today ? stats.ExamDate.Date : DateTime.Today.AddDays(7);
        Window ask = null;

        Func<string, TextBlock> label = text => new TextBlock { Text = text, Foreground = muted, FontFamily = font, FontSize = 12 };
        // A clickable pill; strong = the red one (Save)
        Func<string, bool, Action, Border> button = (text, strong, act) =>
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
                Background = strong ? red : faint,
                Child = new TextBlock { Text = text, Foreground = cream, FontFamily = font, FontSize = 13, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal }
            };
            pill.MouseLeftButtonUp += (s, e) => act();
            return pill;
        };

        var name = new TextBox
        {
            Text = stats.ExamName, MaxLength = 12, FontFamily = font, FontSize = 14, Foreground = cream, CaretBrush = cream,
            Background = faint, BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 6, 0, 14)
        };
        var when = new TextBlock
        {
            Foreground = cream, FontFamily = font, FontSize = 15, FontWeight = FontWeights.SemiBold, MinWidth = 190,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        Action show = () =>
        {
            int d = (date - DateTime.Today).Days;
            when.Text = date.ToString("ddd, MMM d", CultureInfo.InvariantCulture) + "  ·  " + (d == 0 ? "today" : d == 1 ? "tomorrow" : "in " + d + " days");
        };
        Action<int> move = days => { date = date.AddDays(days) < DateTime.Today ? DateTime.Today : date.AddDays(days); show(); };
        show();

        var stepper = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        stepper.Children.Add(button("-7", false, () => move(-7)));
        stepper.Children.Add(button("-1", false, () => move(-1)));
        stepper.Children.Add(when);
        stepper.Children.Add(button("+1", false, () => move(1)));
        stepper.Children.Add(button("+7", false, () => move(7)));

        Action save = () => { SetExam(name.Text, date); ask.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(button("No exam", false, () => { SetExam("", DateTime.MinValue); ask.Close(); }));
        buttons.Children.Add(button("Cancel", false, () => ask.Close()));
        buttons.Children.Add(button("Save", true, save));

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Exam countdown", Foreground = cream, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(label("What's it called? (12 letters at most)"));
        panel.Children.Add(name);
        panel.Children.Add(label("When is it?"));
        panel.Children.Add(stepper);
        panel.Children.Add(buttons);

        ask = new Window
        {
            Title = "Exam countdown", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Topmost = true, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16, 20, 16),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x11, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xC2, 0x3B, 0x30)), BorderThickness = new Thickness(1),
                Child = panel
            }
        };
        // Enter saves, Escape cancels; typing goes straight into the name
        ask.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) save();
            else if (e.Key == Key.Escape) ask.Close();
        };
        ask.Loaded += (s, e) => { name.Focus(); name.SelectAll(); };
        return ask;
    }

    // ---------- After a quiz ----------
    // The quiz was just closed. If a session finished in the last ten minutes, she has an opinion
    // about how it went. (False = nothing to say about it; the usual goodbye line is used.)
    public bool QuizClosed()
    {
        if (stats.LastPct < 0 || (DateTime.Now - stats.LastDone).TotalMinutes > 10) return false;
        if (stats.LastPct >= 80) Respond("aftergood", "fist");
        else if (stats.LastPct < 50) Respond("afterbad", "facepalm");
        else return false;
        return true;
    }

    // ---------- Mood ----------
    // Her mood comes from real things: the hour, how long it's been since a quiz was started, and how
    // the answers have gone (what the Stats class keeps).
    //   sleepy   - late at night, 11 pm to 6 am (bedtime: she lies down and dozes), except around
    //              an hour you usually study in, when she stays up with you
    //   restless - no quiz for two days or more (or ever); half a day when an exam is close; or
    //              it's around your usual study time and you haven't started one in three hours
    //   pleased  - a quiz in the last three hours that went well
    //   bored    - any other time
    public static string MoodFor(DateTime now, Stats s)
    {
        bool studyTime = StudyTime(now, s);
        if ((now.Hour >= 23 || now.Hour < 6) && !studyTime) return "sleepy";
        double hours = (now - s.LastQuiz).TotalHours;
        if (studyTime && hours >= 3) return "restless";
        // With an exam a week or less away she gets restless after half a day, not two
        if (hours >= (ExamDays(now, s) <= 7 ? 12 : 48)) return "restless";
        // How the last session went, if one finished in the last three hours; otherwise all answers so far
        bool wentWell = s.LastPct >= 0 && (now - s.LastDone).TotalHours < 3 ? s.LastPct >= 70 : Accuracy(s) >= 70;
        if (hours < 3 && wentWell) return "pleased";
        return "bored";
    }

    // Your habits: an hour of the day is one you usually study in once at least 3 quizzes have been
    // started in it, and at least a quarter as many as in your busiest hour
    static bool UsualHour(Stats s, int hour)
    {
        int n = s.StudyHours[(hour + 24) % 24];
        return n >= 3 && n * 4 >= s.StudyHours.Max();
    }

    // Around your usual study time: during one of those hours, or the half hour before it
    static bool StudyTime(DateTime now, Stats s)
    {
        return UsualHour(s, now.Hour) || (now.Minute >= 30 && UsualHour(s, now.Hour + 1));
    }

    // Percent of all answers that were right (counts as 100 until there are enough answers to judge)
    static int Accuracy(Stats s) { return s.Answered < 10 ? 100 : 100 * s.Correct / s.Answered; }

    string Mood { get { return MoodFor(DateTime.Now, stats); } }

    // What she settles into next. Sleepy: mostly lying down. Restless: on her feet more.
    string PickPose(string mood)
    {
        double lie = mood == "sleepy" ? 0.75 : mood == "restless" ? 0.2 : 0.42;
        double sit = mood == "sleepy" ? 0.2 : mood == "restless" ? 0.45 : 0.42;
        double r = rng.NextDouble();
        return r < lie ? "lie" : r < lie + sit ? "sit" : "stand";
    }

    // ---------- Small idle moments ----------
    // Between the big pose changes she does something small now and then: glances around, stretches,
    // shifts her weight, or strikes one of the quiz's reaction poses. Which ones depends on her mood.
    // A name listed twice is twice as likely.
    static readonly Dictionary<string, string[]> Moments = new Dictionary<string, string[]>
    {
        { "sleepy", new[] { "yawn", "yawn", "stretch", "glance", "shift" } },
        { "restless", new[] { "cross", "hips", "glance", "shift", "think", "facepalm" } },
        { "pleased", new[] { "glance", "stretch", "shift", "think", "fist" } },
        { "bored", new[] { "glance", "glance", "shift", "stretch", "think", "cross", "hips" } }
    };

    // How many seconds each moment lasts
    static readonly Dictionary<string, double> MomentSeconds = new Dictionary<string, double>
    {
        { "glance", 4 }, { "stretch", 3.6 }, { "shift", 9 }, { "think", 5 }, { "cross", 6 }, { "hips", 5 },
        { "facepalm", 2.4 }, { "point", 2.2 }, { "fist", 1.6 }, { "cheer", 1.8 }, { "yawn", 3.2 }, { "sneeze", 1.4 }, { "dizzy", 2.6 }, { "wave", 2.2 }
    };
    string lastMoment;
    DateTime nextMoment;
    int lastFrames, lastTicks;
    static readonly string FpsLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quizzahh", "fps-log.txt");

    void IdleMoment(string pose)
    {
        string mood = Mood;
        nextMoment = DateTime.Now.AddSeconds(mood == "sleepy" ? 50 + rng.Next(40) : 30 + rng.Next(25));
        if (view.Dozing) { if (rng.Next(2) == 0) Say(Line("doze", mood)); return; } // asleep: no moments, just a "zz" now and then
        // once in a long while, a sneeze (not part of any mood's moments: it just happens)
        if (rng.Next(60) == 0 && HeatherView.Fits("sneeze", pose)) { Act("sneeze"); Say(rng.Next(2) == 0 ? "Achoo." : "Achoo. ...Sorry."); return; }
        var pool = Moments[mood].ToList();
        if (Accuracy(stats) < 60) pool.Add("facepalm"); // the answers haven't been going well
        string m = PickMoment(pool, pose);
        Act(m);
        if (m == "yawn" && rng.Next(3) == 0) { Say("*yawn*"); return; }
        if (rng.Next(6) == 0) Say(Line("idle", mood)); // now and then she mutters something
    }

    // A moment from the pool that suits the pose she's in (null if none does). Not the one she did
    // last, unless that's the only one that fits.
    string PickMoment(IEnumerable<string> pool, string pose)
    {
        var fit = pool.Where(m => HeatherView.Fits(m, pose)).ToList();
        if (fit.Any(m => m != lastMoment)) fit.RemoveAll(m => m == lastMoment);
        return fit.Count == 0 ? null : fit[rng.Next(fit.Count)];
    }

    // Play a moment. Glances start toward the middle of the screen, where things happen.
    void Act(string moment)
    {
        if (moment == null) return;
        lastMoment = moment;
        var m = MonitorAt(Left + W / 2, Top + GroundY - 1);
        view.MomentDir = Left + W / 2 < m.Left + m.Width / 2 ? 1 : -1;
        view.React(moment, MomentSeconds[moment]);
    }

    // ---------- Noticing the mouse ----------
    // When the mouse comes near her she looks over and her head follows it. After a few seconds she
    // loses interest. She won't notice again until it has gone away and come back, and at most once
    // every 40 seconds.
    const double NearDip = 300;
    bool wasNear;
    DateTime watchUntil, nextNotice = DateTime.MinValue;

    // Where the mouse is, measured from her head: X to the right, Y upward (in DIPs).
    // null while her window isn't on screen, because then there's nothing to measure from.
    Vector? MouseFromHead()
    {
        if (PresentationSource.FromVisual(this) == null) return null;
        var c = System.Windows.Forms.Cursor.Position;
        var m = PointFromScreen(new Point(c.X, c.Y));
        var head = view.HeadPoint();
        return new Vector(m.X - head.X, head.Y - m.Y);
    }

    // Once a second: start watching when the mouse arrives, stop when the time is up
    void NoticeTick(Vector? mouse, DateTime now)
    {
        bool near = mouse.HasValue && mouse.Value.Length < NearDip;
        string settled = view.Settled;
        bool can = !dragging && settled != null && settled != "walk";
        if (near && !wasNear && can && now >= nextNotice) Watch(now, 4 + rng.Next(4));
        else if (view.Watching && (!can || now >= watchUntil)) view.Watching = false;
        wasNear = near;
    }

    void Watch(DateTime now, int seconds)
    {
        view.Watching = true;
        watchUntil = now.AddSeconds(seconds);
        nextNotice = now.AddSeconds(40);
        // No idle moment right on top of this: keep her quiet for a while after
        var quiet = now.AddSeconds(seconds + 15);
        if (nextMoment < quiet) nextMoment = quiet;
    }

    // Every frame while she's watching: turn her head toward the mouse. Atan2 turns "this far across,
    // that far away" into an angle, so a mouse far off to the side levels out instead of twisting her neck.
    void AimGaze(Vector? mouse)
    {
        if (!mouse.HasValue) return;
        view.GazeYaw = Math.Max(-50, Math.Min(50,Math.Atan2(mouse.Value.X, 200) * 180 / Math.PI));
        view.GazePitch = Math.Max(-20, Math.Min(35, Math.Atan2(mouse.Value.Y, 260) * 180 / Math.PI));
    }

    // ---------- Self-test (CS-Quiz-test.exe --snap <dir>) ----------
    // How far her arms and legs pass into her body. Her body, from her hips to her neck, is measured as a
    // stack of 8 ovals, each as wide and as deep as she is at that height; a point of an arm or a leg more
    // than 30% of the way in from the edge of its oval is inside her. Returns the most points of any one
    // kind (an arm into her body, a hand into a thigh...).
    static int Clipping(HeatherView v, out string worst)
    {
        var M = v.Model;
        var lo = M.JointPos("bone003") + (M.JointPos("bone005") - M.JointPos("bone003")) * 0.5;
        var up = M.JointPos("bone006") - lo;
        double len = up.Length; up /= len;
        var across = M.JointPos("bone026") - M.JointPos("bone032");
        across -= up * Vector3D.DotProduct(across, up); across.Normalize();
        var deep = Vector3D.CrossProduct(up, across);
        Func<string, string> part = n => n == "" ? "" : M.Under(n, "bone026") ? "left arm" : M.Under(n, "bone032") ? "right arm"
            : HeatherView.Skirt.Contains(n) ? "skirt" : M.Under(n, "bone003") ? "left leg" : M.Under(n, "bone005") ? "right leg" : M.Under(n, "bone009") ? "head" : "body";
        var hw = new List<double>[8]; var hd = new List<double>[8];
        for (int i = 0; i < 8; i++) { hw[i] = new List<double>(); hd[i] = new List<double>(); }
        var limbs = new List<KeyValuePair<string, double[]>>();
        foreach (var kv in M.SomePointsByJoint())
        {
            var d = kv.Value - lo;
            double t = Vector3D.DotProduct(d, up) / len, a = Vector3D.DotProduct(d, across), dp = Vector3D.DotProduct(d, deep);
            string who = part(kv.Key);
            if (who == "body") { if (t >= 0 && t < 1) { hw[(int)(t * 8)].Add(Math.Abs(a)); hd[(int)(t * 8)].Add(Math.Abs(dp)); } }
            else if (who.EndsWith("arm") || who.EndsWith("leg")) limbs.Add(new KeyValuePair<string, double[]>(who, new[] { t, a, dp }));
        }
        Func<List<double>, double> p90 = l => { if (l.Count < 5) return 0; l.Sort(); return l[(int)(l.Count * 0.9)]; };
        var w = hw.Select(p90).ToArray(); var dd = hd.Select(p90).ToArray();
        var count = new Dictionary<string, int>(); var deepest = new Dictionary<string, double>();
        foreach (var lp in limbs)
        {
            double t = lp.Value[0];
            if (t < (lp.Key.EndsWith("leg") ? 0.25 : 0.05) || t > 0.85) continue; // (where the limbs join the body, they are meant to touch)
            int bin = (int)(t * 8);
            if (w[bin] <= 0 || dd[bin] <= 0) continue;
            double e = Math.Sqrt(Math.Pow(lp.Value[1] / w[bin], 2) + Math.Pow(lp.Value[2] / dd[bin], 2));
            if (e >= 0.7) continue;
            int c; count.TryGetValue(lp.Key, out c); count[lp.Key] = c + 1;
            double was; deepest.TryGetValue(lp.Key, out was); deepest[lp.Key] = Math.Max(was, 1 - e);
        }
        // ...and every part against every other part. Each piece of an arm or a leg is a rounded tube
        // (upper arm, forearm, hand, thigh, shin, foot), her head a ball, each as thick as its own skin
        // (the 80th-percentile distance of its own points from its middle line). A point of another
        // part more than 30% of the way into one of them is passing through it.
        var all = M.SomePointsByJoint().Select(kv => new KeyValuePair<string, Point3D>(kv.Key, kv.Value)).ToList();
        var pieces = new List<Tube>();
        Func<string, string, string, string, Tube> tube = (who, name, top, below) =>
        {
            var own = all.Where(kv => kv.Key != "" && M.Under(kv.Key, top) && (below == null || !M.Under(kv.Key, below)) && !HeatherView.Skirt.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            if (own.Count < 5) return null;
            var a = M.JointPos(top);
            Point3D bEnd;
            if (below != null) bEnd = M.JointPos(below);
            else { var c = new Point3D(own.Average(q => q.X), own.Average(q => q.Y), own.Average(q => q.Z)); bEnd = a + (c - a) * 2; } // (a hand or a foot: out past its middle)
            var tb = new Tube { Part = who, Name = name, A = a, B = bEnd };
            var ds = own.Select(tb.Distance).OrderBy(x => x).ToList();
            // (a hand or a boot is flat, not round: its typical thickness, not its widest, or anything
            // resting on it would count as inside it)
            tb.R = ds[(int)(ds.Count * (below == null ? 0.5 : 0.8))];
            return tb;
        };
        foreach (var side in new[] { new[] { "left", "bone026", "bone034", "bone039", "bone003", "bone007", "bone021" }, new[] { "right", "bone032", "bone036", "bone041", "bone005", "bone008", "bone027" } })
        {
            pieces.Add(tube(side[0] + " arm", side[0] + " upper arm", side[1], side[2]));
            pieces.Add(tube(side[0] + " arm", side[0] + " forearm", side[2], side[3]));
            // (no tube for a hand: it's flat, so a tube round it is far fatter than the hand, and a cheek
            // resting on it would count as inside it. A hand going into anything else still counts.)
            pieces.Add(tube(side[0] + " leg", side[0] + " thigh", side[4], side[5]));
            pieces.Add(tube(side[0] + " leg", side[0] + " shin", side[5], side[6]));
            pieces.Add(tube(side[0] + " leg", side[0] + " foot", side[6], null));
        }
        var headPts = all.Where(kv => kv.Key != "" && M.Under(kv.Key, "bone009")).Select(kv => kv.Value).ToList();
        if (headPts.Count >= 5)
        {
            var hc = new Point3D(headPts.Average(q => q.X), headPts.Average(q => q.Y), headPts.Average(q => q.Z));
            var head = new Tube { Part = "head", Name = "head", A = hc, B = hc };
            var hd2 = headPts.Select(head.Distance).OrderBy(x => x).ToList();
            head.R = hd2[(int)(hd2.Count * 0.45)]; // (her hair puffs out well past her face, where a hand can rest)
            pieces.Add(head);
        }
        pieces.RemoveAll(x => x == null);
        foreach (var kv in all)
        {
            string who = part(kv.Key);
            if (who == "" || who == "body") continue;
            foreach (var tb in pieces)
            {
                if (tb.Part == who) continue;
                // where the skirt sits on the tops of her thighs it's meant to touch them
                if (who == "skirt" && !tb.Name.EndsWith("thigh")) continue;
                double tAlong;
                double dist = tb.Distance(kv.Value, out tAlong);
                if (who == "skirt" && tAlong < 0.35) continue;
                if (dist >= 0.7 * tb.R) continue;
                string key = (who == "skirt" ? "her " + tb.Name + " through her skirt" : who + " into " + tb.Name);
                int c; count.TryGetValue(key, out c); count[key] = c + 1;
                double was; deepest.TryGetValue(key, out was); deepest[key] = Math.Max(was, 1 - dist / tb.R);
            }
        }
        worst = string.Join(", ", count.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value + " points, up to " + (deepest[kv.Key] * 100).ToString("0") + "% in"));
        return count.Count == 0 ? 0 : count.Values.Max();
    }

    // A rounded tube from A to B (a ball when A = B), for the clipping check
    class Tube
    {
        public string Part, Name;
        public Point3D A, B;
        public double R;
        public double Distance(Point3D q) { double t; return Distance(q, out t); }
        public double Distance(Point3D q, out double t)
        {
            var ab = B - A;
            double len2 = ab.LengthSquared;
            t = len2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, Vector3D.DotProduct(q - A, ab) / len2));
            return (q - (A + ab * t)).Length;
        }
    }

    // Builds her without ever showing the window, checks her rules in code, and saves contact sheets
    // of her poses (buddy-*.png) to look over.
    public static void SelfTest(HeatherModel model, string dir, System.Text.StringBuilder log, Action<string> bad)
    {
        var st = new Stats();
        var b = new BuddyWindow(model, st, () => { }) { Suppressed = true };
        // No real windows during these checks (she'd land on them); made-up ones come in further down
        var realWindows = Windows;
        var realFrameOf = FrameOf;
        Windows = () => new List<KeyValuePair<IntPtr, Rect>>();
        FrameOf = h => null;
        // No part of her drawn body may reach the edge of her window (a window can't draw outside itself)
        var reach = Rect.Empty;
        // ...and no arm or leg passes through her body (worst case kept per kind of picture)
        var clips = new Dictionary<string, KeyValuePair<int, string>>();
        Action<string> inWindow = what =>
        {
            string how;
            int n = Clipping(b.view, out how);
            string kind = what.Split(',')[0];
            KeyValuePair<int, string> had;
            if (n > 0 && (!clips.TryGetValue(kind, out had) || n > had.Key)) clips[kind] = new KeyValuePair<int, string>(n, how);
            var e = b.view.Extent();
            reach.Union(e);
            if (e.Left < 2 || e.Top < 2 || e.Right > W - 2 || e.Bottom > H - 2)
                bad("buddy: " + what + ", she's cut off by her window (she fills " + e.Left.ToString("0") + "," + e.Top.ToString("0") + " to " + e.Right.ToString("0") + "," + e.Bottom.ToString("0") + " of " + W + "x" + H + ")");
        };
        try
        {
            log.AppendLine("Buddy: real windows she could sit on right now: " + string.Join("  ", realWindows().Select(w => w.Value.Width + "x" + w.Value.Height + " at " + w.Value.Left + "," + w.Value.Top)));
            foreach (var pose in new[] { "stand", "sit", "lie" })
            {
                // One picture of every moment that fits this pose, next to the plain pose
                var cells = new List<KeyValuePair<string, ImageSource>>();
                foreach (var m in new string[] { null }.Concat(MomentSeconds.Keys))
                    if (m == null || HeatherView.Fits(m, pose))
                    {
                        cells.Add(new KeyValuePair<string, ImageSource>(pose + ": " + (m ?? "(plain)"), b.Shot(pose, m)));
                        inWindow(pose + " " + (m ?? ""));
                    }
                Sheet(dir, "buddy-" + pose + ".png", cells, 6);

                // In every mood she never picks a moment that doesn't fit, and never the same one twice
                // in a row (unless only one fits)
                foreach (var pool in Moments)
                {
                    bool choice = pool.Value.Where(m => HeatherView.Fits(m, pose)).Distinct().Count() > 1;
                    for (int i = 0; i < 300; i++)
                    {
                        string before = b.lastMoment, m = b.PickMoment(pool.Value, pose);
                        if (m == null || !HeatherView.Fits(m, pose)) bad("buddy: " + pool.Key + " picked the moment '" + m + "' while in " + pose);
                        if (m == before && choice) bad("buddy: the moment '" + m + "' twice in a row");
                        b.lastMoment = m;
                    }
                }
            }
            log.AppendLine("Buddy: " + MomentSeconds.Count + " moments checked in 3 poses");

            // Noticing the mouse: pictures of her head following it, then the rules
            var gaze = new List<KeyValuePair<string, ImageSource>>();
            foreach (var pose in new[] { "stand", "sit", "lie" })
                foreach (var at in new[] { new Vector(-400, 250), new Vector(0, 300), new Vector(400, 250), new Vector(250, -40) })
                {
                    b.view.Watching = true;
                    b.AimGaze(at);
                    gaze.Add(new KeyValuePair<string, ImageSource>(pose + ": mouse at " + at.X + ", " + at.Y, b.Shot(pose, null)));
                }
            Sheet(dir, "buddy-gaze.png", gaze, 4);
            b.view.Watching = false;
            b.view.SnapTo("sit");
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
            Vector far = new Vector(900, 300), close = new Vector(120, 80);
            b.NoticeTick(far, t0);
            if (b.view.Watching) bad("buddy: watched a mouse that was far away");
            b.NoticeTick(close, t0.AddSeconds(1));
            if (!b.view.Watching) bad("buddy: didn't notice the mouse coming near");
            b.NoticeTick(close, t0.AddSeconds(10));
            if (b.view.Watching) bad("buddy: never lost interest in the mouse");
            b.NoticeTick(far, t0.AddSeconds(11));
            b.NoticeTick(close, t0.AddSeconds(12));
            if (b.view.Watching) bad("buddy: noticed the mouse again too soon");
            b.NoticeTick(far, t0.AddSeconds(50));
            b.NoticeTick(close, t0.AddSeconds(51));
            if (!b.view.Watching) bad("buddy: didn't notice the mouse the second time");
            b.view.Watching = false;
            log.AppendLine("Buddy: mouse noticing checked");

            // Moods: the hour, the time since the last quiz, and the answers so far
            var noon = new DateTime(2026, 1, 5, 12, 0, 0);
            Action<string, DateTime, DateTime, int, int> mood = (want, now, lastQuiz, answered, correct) =>
            {
                string got = MoodFor(now, new Stats { LastQuiz = lastQuiz, Answered = answered, Correct = correct });
                if (got != want) bad("buddy: mood should be " + want + " but is " + got + " (" + now + ", last quiz " + lastQuiz + ", " + correct + "/" + answered + ")");
            };
            mood("sleepy", noon.AddHours(14), noon, 50, 45);            // 2 am, even right after a good quiz
            mood("sleepy", noon.AddHours(11.5), noon, 50, 45);          // 11:30 pm
            mood("restless", noon, DateTime.MinValue, 0, 0);            // never studied
            mood("restless", noon, noon.AddDays(-3), 50, 45);           // three days ago
            mood("pleased", noon, noon.AddHours(-1), 50, 45);           // an hour ago, 90% right
            mood("pleased", noon, noon.AddHours(-1), 4, 0);             // too few answers to judge
            mood("bored", noon, noon.AddHours(-1), 50, 20);             // an hour ago, but 40% right
            mood("bored", noon, noon.AddHours(-10), 50, 45);            // this morning
            // the last session counts for more than the lifetime average, and a close exam makes her impatient
            Func<Stats, string> moodOf = made => MoodFor(noon, made);
            if (moodOf(new Stats { LastQuiz = noon.AddHours(-1), Answered = 200, Correct = 80, LastPct = 90, LastDone = noon.AddMinutes(-30) }) != "pleased") bad("buddy: a good session just now should please her, whatever the lifetime average");
            if (moodOf(new Stats { LastQuiz = noon.AddHours(-1), Answered = 200, Correct = 190, LastPct = 30, LastDone = noon.AddMinutes(-30) }) != "bored") bad("buddy: a bad session just now shouldn't please her");
            if (moodOf(new Stats { LastQuiz = noon.AddHours(-20), ExamDate = noon.AddDays(3) }) != "restless") bad("buddy: 20 hours without a quiz and an exam in 3 days should make her restless");
            if (moodOf(new Stats { LastQuiz = noon.AddHours(-20), ExamDate = noon.AddDays(30) }) != "bored") bad("buddy: 20 hours without a quiz is fine when the exam is a month off");
            Action<string, int> exam = (want, days) =>
            {
                string got = ExamLine(noon, new Stats { ExamName = "Midterm", ExamDate = noon.Date.AddDays(days).AddHours(9) });
                if (got != want) bad("buddy: exam line for " + days + " days should be '" + want + "' but is '" + got + "'");
            };
            exam("Midterm in 9 days.", 9); exam("Midterm is tomorrow.", 1); exam("Midterm is today. Go.", 0); exam(null, -1);
            if (ExamLine(noon, new Stats()) != null) bad("buddy: counted down to an exam that was never set");

            // Habits: she learns which hours you study in. A night owl who studies at 1 am keeps her up then.
            var owl = new Stats { LastQuiz = noon.AddHours(-6), Answered = 50, Correct = 45 };
            for (int i = 0; i < 10; i++) owl.Started(noon.Date.AddHours(1).AddMinutes(i));  // ten quizzes started at 1 am
            for (int i = 0; i < 12; i++) owl.Started(noon.Date.AddHours(20).AddMinutes(i)); // twelve at 8 pm
            owl.StudyHours[15] = 2;                                                          // only twice at 3 pm
            owl.LastQuiz = noon.AddHours(-6);
            Action<string, DateTime, string> habit = (want, now, why) =>
            {
                string got = MoodFor(now, owl);
                if (got != want) bad("buddy: " + why + ", her mood should be " + want + " but is " + got);
            };
            habit("restless", noon.Date.AddDays(1).AddHours(1.25), "1:15 am is your usual study hour (no quiz since noon)");
            habit("sleepy", noon.Date.AddDays(1).AddHours(3.25), "3:15 am isn't one of your hours");
            habit("restless", noon.Date.AddHours(19.6), "half an hour before your usual 8 pm");
            habit("bored", noon.Date.AddHours(19.2), "an hour before your usual 8 pm");
            habit("bored", noon.Date.AddHours(15.2), "3 pm, studied there only twice");
            owl.LastQuiz = noon.Date.AddHours(19);
            habit("pleased", noon.Date.AddHours(20.2), "you started a quiz an hour before your usual time");
            var fading = new Stats();
            fading.StudyHours[9] = 30;
            for (int i = 0; i < 40; i++) fading.Started(noon.Date.AddHours(20)); // the 40th halves everything
            if (fading.StudyHours[20] != 20 || fading.StudyHours[9] != 15) bad("buddy: old habits should fade (8 pm " + fading.StudyHours[20] + ", 9 am " + fading.StudyHours[9] + ")");
            // the once-a-day nudge: at your usual study time...
            var keepHours = st.StudyHours; var keepLast = st.LastQuiz; var keepExam = st.ExamDate; var keepName = st.ExamName;
            var keepMissed = b.Missed; var keepReview = b.Review;
            st.ExamDate = DateTime.MinValue;
            st.StudyHours = new int[24]; st.StudyHours[20] = 5;
            st.LastQuiz = noon.Date.AddHours(14);
            if (!b.NudgeDue(noon.Date.AddHours(20.1))) bad("buddy: didn't mention your usual study time");
            if (b.NudgeDue(noon.Date.AddHours(20.5))) bad("buddy: mentioned your usual study time twice in a day");
            if (!b.NudgeDue(noon.Date.AddDays(1).AddHours(20.1))) bad("buddy: didn't mention your usual study time the next day");
            if (b.NudgeDue(noon.Date.AddDays(2).AddHours(12))) bad("buddy: nudged at noon with no exam coming and no habit then");
            st.LastQuiz = noon.Date.AddDays(3).AddHours(19.5);
            if (b.NudgeDue(noon.Date.AddDays(3).AddHours(20.1))) bad("buddy: nudged right after you started a quiz");
            // ...or any time in the week before your exam, but never at bedtime
            st.StudyHours = new int[24]; st.LastQuiz = noon.Date.AddDays(3);
            st.ExamName = "Midterm"; st.ExamDate = noon.Date.AddDays(14);
            if (!b.NudgeDue(noon.Date.AddDays(10).AddHours(15))) bad("buddy: no nudge 4 days before the exam");
            if (b.NudgeDue(noon.Date.AddDays(11).AddHours(2))) bad("buddy: nudged you at 2 am");
            if (b.NudgeDue(noon.Date.AddDays(4).AddHours(15))) bad("buddy: nudged 10 days before the exam with no habit then");
            // what the nudge says, and what a click on her then does
            int redo = 0;
            b.Review = () => redo++;
            b.view.SnapTo("sit");
            b.Missed = () => 7;
            b.Nudge(st.ExamDate.Date.AddDays(-4).AddHours(15));
            if (b.bubbleText.Text != "Midterm in 4 days. Redo misses?") bad("buddy: exam nudge said: " + b.bubbleText.Text);
            b.Clicked();
            b.Missed = () => 0;
            b.Nudge(st.ExamDate.Date.AddDays(-1).AddHours(15));
            if (b.bubbleText.Text != "Midterm tomorrow. Quiz?") bad("buddy: exam nudge with nothing missed said: " + b.bubbleText.Text);
            if (b.offering) bad("buddy: offered a review with nothing missed");
            st.ExamDate = DateTime.MinValue; st.StudyHours[20] = 5;
            b.Missed = () => 3;
            b.Nudge(noon.Date.AddHours(20.1));
            if (b.bubbleText.Text != "Your usual hour. Redo misses?") bad("buddy: habit nudge said: " + b.bubbleText.Text);
            b.Clicked();
            if (redo != 2) bad("buddy: a click after a nudge offering a review should start it (" + redo + " of 2)");
            st.StudyHours = keepHours; st.LastQuiz = keepLast; st.ExamDate = keepExam; st.ExamName = keepName;
            b.Missed = keepMissed; b.Review = keepReview; b.offering = false;
            log.AppendLine("Buddy: habits checked (night-owl hours, half hour before, fading, once-a-day nudge, exam week, review offer)");
            var lies = new Dictionary<string, int>();
            foreach (var m in Moments.Keys)
            {
                lies[m] = 0;
                for (int i = 0; i < 2000; i++) if (b.PickPose(m) == "lie") lies[m]++;
            }
            if (lies["sleepy"] < 1300) bad("buddy: sleepy should mostly lie down (" + lies["sleepy"] + " of 2000)");
            if (lies["restless"] > 600) bad("buddy: restless should rarely lie down (" + lies["restless"] + " of 2000)");
            log.AppendLine("Buddy: lies down " + string.Join(", ", lies.Select(kv => kv.Key + " " + kv.Value / 20 + "%")));

            // Lines: every pool is real, has a choice, and keeps her voice (six words at most)
            Func<string, int> words = l => l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            foreach (var pool in Pool)
            {
                var key = pool.Key.Split('.');
                if (!Pool.ContainsKey(key[0]) || (key.Length > 1 && !Moments.ContainsKey(key[1]))) bad("buddy: no such situation or mood: " + pool.Key);
                if (pool.Value.Length < 2) bad("buddy: the line pool " + pool.Key + " needs at least two lines");
                if (pool.Value.Distinct().Count() != pool.Value.Length) bad("buddy: the line pool " + pool.Key + " repeats a line");
                foreach (var l in pool.Value) if (words(l) < 1 || words(l) > 6) bad("buddy: line isn't 1-6 words: " + l);
            }
            // ...and with real numbers behind her, in every situation and mood, she never repeats herself
            st.Answered = 200; st.Correct = 95; st.BestStreak = 12; st.Xp = 15000;
            st.LastQuiz = DateTime.Now.AddDays(-5); st.LastClass = "Bio 1 - General Biology";
            var said = new HashSet<string>();
            foreach (var context in Pool.Keys.Where(k => !k.Contains(".")))
                foreach (var m in Moments.Keys)
                    for (int i = 0; i < 300; i++)
                    {
                        string before = b.lastLine, l = b.Line(context, m);
                        if (l == before) bad("buddy: said the same line twice in a row: " + l);
                        if (words(l) > 6) bad("buddy: line is over six words: " + l);
                        said.Add(l);
                    }
            log.AppendLine("Buddy: " + said.Count + " different lines said (" + Pool.Values.Sum(p => p.Length) + " written, the rest from your stats)");

            // What you do to her: each gets a line from the right pool and a moment that fits her pose
            Action<string, string, string> reacted = (what, context, moment) =>
            {
                if (!Pool.Where(p => p.Key.Split('.')[0] == context).Any(p => p.Value.Contains(b.bubbleText.Text)))
                    bad("buddy: " + what + " should get a '" + context + "' line, got: " + b.bubbleText.Text);
                if (b.lastMoment != moment) bad("buddy: " + what + " should play '" + moment + "', played '" + b.lastMoment + "'");
            };
            // picked up: a line, and she dangles (no pose of her own); put down gently: a line and she settles
            b.view.SnapTo("sit"); b.PickUp(null);
            if (!b.view.Held || !b.dragging) bad("buddy: picked up, she should be dangling from your hand");
            if (!Pool["drag"].Concat(Pool["drag.sleepy"]).Contains(b.bubbleText.Text)) bad("buddy: picked up, she should complain, said: " + b.bubbleText.Text);
            // the part she's held by stays under the mouse: the window moves to make it so
            var mouseOnScreen = new Point(b.Left + 400, b.Top + 300);
            b.PinGrab(mouseOnScreen);
            var part = b.view.GrabPoint();
            if (Math.Abs(b.Left + part.X - mouseOnScreen.X) > 0.5 || Math.Abs(b.Top + part.Y - mouseOnScreen.Y) > 0.5)
                bad("buddy: the part she's held by should be under the mouse at " + mouseOnScreen + ", but it's at " + (b.Left + part.X).ToString("0") + "," + (b.Top + part.Y).ToString("0"));
            // Carrying her slowly while she swings must never read as a throw. Held by a foot she swings
            // hard and her window moves a lot; the pointer itself only drifts at 60 units a second.
            b.LetGo(new Vector()); b.landing = false;
            b.view.SnapTo("stand"); b.PickUp(null); b.view.Grabbed = "bone021";
            var hand = new Point(600, 250); // (mid-screen, away from the walls, and high enough that she fits hanging below it with room to fall)
            b.carryAt = hand; b.carryTime = 0;
            double ct0 = b.view.Now + 400, fastest = 0;
            b.view.Step(ct0);
            for (int i = 1; i <= 180; i++)
            {
                hand.X += 1;
                b.Carried(hand, i / 60.0);
                b.view.Step(ct0 + i / 60.0);
                b.PinGrab(hand);
                fastest = Math.Max(fastest, b.carryV.Length);
                var foot = b.view.GrabPoint();
                if (Math.Abs(b.Left + foot.X - hand.X) > 0.5 || Math.Abs(b.Top + foot.Y - hand.Y) > 0.5) { bad("buddy: the foot she's carried by slipped out of your hand"); break; }
            }
            if (fastest > 120) bad("buddy: carried at 60 units a second, her speed read as " + fastest.ToString("0") + " (her own swinging is being counted)");
            b.LetGo(b.carryV);
            if (b.falling) bad("buddy: put down after a slow carry, she was thrown");
            b.landing = false; b.view.Held = false; b.view.Grabbed = "bone009";
            double rt = b.view.Now + 500;
            b.view.Step(rt); b.view.Step(rt + 1); b.view.Step(rt + 2);
            // thrown while hanging upside down by a foot: her middle carries on from where it was, no lurch
            b.view.SnapTo("stand"); b.PickUp(null); b.view.Grabbed = "bone021";
            double ut = b.view.Now + 600;
            b.view.Step(ut);
            for (int i = 1; i <= 180; i++) { b.view.Step(ut + i / 60.0); b.PinGrab(hand); }
            b.view.Grabbed = "bone006";
            var chestBefore = b.view.GrabPoint();
            var chestOnScreen = new Point(b.Left + chestBefore.X, b.Top + chestBefore.Y);
            b.view.Grabbed = "bone021";
            b.LetGo(new Vector(900, -300));
            b.view.Step(ut + 3 + 1 / 120.0);
            var chestAfter = b.view.GrabPoint();
            double lurch = Math.Abs(b.Left + chestAfter.X - chestOnScreen.X) + Math.Abs(b.Top + chestAfter.Y - chestOnScreen.Y);
            if (lurch > 30) bad("buddy: thrown from upside down, she lurched " + lurch.ToString("0") + " units in one frame (chest was at " + chestOnScreen + ", now " + (b.Left + chestAfter.X).ToString("0") + "," + (b.Top + chestAfter.Y).ToString("0") + "; path point " + b.fallAt + ", speed " + b.fallV + ", falling " + b.falling + ", landing " + b.landing + ", hand " + hand + ")");
            for (int i = 2; i <= 120 * 20 && (b.falling || b.landing); i++) b.view.Step(ut + 3 + i / 120.0);
            if (b.falling || b.landing) bad("buddy: thrown from upside down, she never landed");
            b.view.SnapTo("sit"); b.PickUp(null);
            // let go gently: she rights herself (a few frames of made-up time), then she's put down
            b.LetGo(new Vector(40, 10));
            if (!b.landing) bad("buddy: let go gently, she should be righting herself");
            double lt = b.view.Now + 300;
            b.view.Step(lt);
            for (int i = 1; i <= 240 && b.landing; i++) b.view.Step(lt + i / 60.0);
            if (b.landing) bad("buddy: never finished righting herself after being put down");
            reacted("putting her down (sitting)", "drop", "glance");
            if (b.view.Held || b.dragging || b.falling) bad("buddy: put down gently, she should be standing on her own again");
            b.view.SnapTo("stand"); b.Respond("drop", "shift", "glance"); reacted("putting her down (standing)", "drop", "shift");
            b.view.SnapTo("lie"); b.Respond("drop", "shift", "glance"); reacted("putting her down (lying)", "drop", "glance");
            b.view.SnapTo("sit"); b.ComeBack(true); reacted("bringing her back", "back", "cheer");
            b.view.SnapTo("lie"); b.ComeBack(false); reacted("her snooze ending", "back", "stretch");
            b.view.SnapTo("stand"); b.WelcomeBack("restless"); reacted("coming back to the PC", "return", "point");
            if (!b.view.Watching) bad("buddy: didn't look over when you came back");
            b.view.Watching = false;
            b.view.SnapTo("lie"); b.WelcomeBack("bored"); reacted("coming back while she's lying down", "return", "glance");
            b.view.SnapTo("stand"); b.WelcomeBack("bored"); reacted("coming back to the PC (not restless)", "return", "wave");
            {
                // the waving hand is up above her shoulder, and it moves
                double wt = b.view.Now, top = double.MaxValue, minX = double.MaxValue, maxX = double.MinValue;
                double shoulderY = 0;
                for (int i = 0; i <= 90; i++)
                {
                    b.view.Step(wt + i / 60.0);
                    if (i < 40 || i > 90) continue; // (the middle of the wave, once the arm is up)
                    var hnd = b.view.ScreenPoint("bone039");
                    shoulderY = b.view.ScreenPoint("bone026").Y;
                    top = Math.Min(top, hnd.Y); minX = Math.Min(minX, hnd.X); maxX = Math.Max(maxX, hnd.X);
                }
                if (top > shoulderY - 20 || maxX - minX < 8) bad("buddy: waving, her hand should be up and moving (hand at " + top.ToString("0") + ", shoulder " + shoulderY.ToString("0") + ", side to side " + (maxX - minX).ToString("0") + ")");
            }
            b.view.Watching = false;
            if (b.BackFromAway(10) || b.BackFromAway(400) || b.BackFromAway(900)) bad("buddy: thought you came back while you were still here or away");
            if (!b.BackFromAway(1)) bad("buddy: didn't see you come back after 15 minutes away");
            if (b.BackFromAway(1)) bad("buddy: welcomed you back twice");
            log.AppendLine("Buddy: drag, drop, snooze and coming-back reactions checked");

            // Petting: four quick rubs back and forth get one reaction (a line, a hand to her head), and not again straight away
            var pt = new DateTime(2026, 1, 1, 12, 0, 0);
            b.view.SnapTo("stand");
            b.bubbleText.Text = "";
            b.lastMoment = null;
            for (int i = 0; i < 3; i++) b.Petting(new Point(i % 2 == 0 ? 100 : 60, 200), pt.AddSeconds(i * 0.2));
            if (b.bubbleText.Text != "") bad("buddy: reacted to petting too soon");
            for (int i = 3; i < 6; i++) b.Petting(new Point(i % 2 == 0 ? 100 : 60, 200), pt.AddSeconds(i * 0.2));
            if (!Pool["pet"].Contains(b.bubbleText.Text)) bad("buddy: didn't react to being petted, said: " + b.bubbleText.Text);
            if (b.lastMoment != "facepalm") bad("buddy: petted while standing she should put a hand to her head, did: " + b.lastMoment);
            b.bubbleText.Text = "";
            for (int i = 0; i < 12; i++) b.Petting(new Point(100, i % 2 == 0 ? 200 : 160), pt.AddSeconds(2 + i * 0.2));
            if (b.bubbleText.Text != "") bad("buddy: reacted to petting again within 12 seconds");
            for (int i = 0; i < 6; i++) b.Petting(new Point(100, i % 2 == 0 ? 200 : 160), pt.AddSeconds(30 + i * 0.2));
            if (!Pool["pet"].Contains(b.bubbleText.Text)) bad("buddy: stroking up and down should count as petting too");
            // rubbing passes over her again and again: her reaction stays up, not swapped for hover lines
            string petLine = b.bubbleText.Text;
            b.Hovered(DateTime.Now);
            if (b.bubbleText.Text != petLine) bad("buddy: passing over her again replaced her petting reaction with: " + b.bubbleText.Text);
            b.saidAt = DateTime.MinValue; b.hoverAt = DateTime.MinValue;
            b.Hovered(DateTime.Now);
            if (b.bubbleText.Text == petLine) bad("buddy: no hover line once she'd finished talking");
            b.Hovered(DateTime.Now.AddSeconds(1));
            if (b.hoverAt > DateTime.Now) bad("buddy: a second hover line within 3 seconds");
            // Break reminder: 50 minutes without a two-minute pause, once
            b.activeSince = pt;
            if (b.BreakDue(pt.AddMinutes(49), 5)) bad("buddy: break reminder came early");
            if (!b.BreakDue(pt.AddMinutes(51), 5)) bad("buddy: no break reminder after 51 minutes");
            if (b.BreakDue(pt.AddMinutes(52), 5)) bad("buddy: break reminder repeated straight away");
            if (b.BreakDue(pt.AddMinutes(100), 300) || b.BreakDue(pt.AddMinutes(140), 5)) bad("buddy: break reminder ignored a pause away from the keyboard");
            // After a quiz: an opinion only when a session just finished, and only when it was clearly good or bad
            st.LastPct = 90; st.LastDone = DateTime.Now.AddMinutes(-2); b.view.SnapTo("sit");
            if (!b.QuizClosed() || !Pool["aftergood"].Contains(b.bubbleText.Text)) bad("buddy: a 90% session just finished, she should say so");
            st.LastPct = 30;
            if (!b.QuizClosed() || !Pool["afterbad"].Contains(b.bubbleText.Text)) bad("buddy: a 30% session just finished, she should say so");
            st.LastPct = 65;
            if (b.QuizClosed()) bad("buddy: commented on a middling session");
            st.LastPct = 90; st.LastDone = DateTime.Now.AddHours(-2);
            if (b.QuizClosed()) bad("buddy: commented on a session from two hours ago");
            st.LastPct = -1;
            log.AppendLine("Buddy: petting, break reminder, exam countdown and after-quiz reactions checked");

            // Bedtime: lying down late at night she dozes; hovering wakes her, and she stays up a little while
            b.view.SnapTo("lie"); b.view.Looking = false; b.view.Watching = false; b.wokeAt = DateTime.MinValue;
            if (!b.ShouldDoze(DateTime.Now, "sleepy")) bad("buddy: lying down at bedtime, she should doze");
            if (b.ShouldDoze(DateTime.Now, "bored")) bad("buddy: dozed off in the daytime");
            b.view.Dozing = true; b.saidAt = DateTime.MinValue;
            b.Hovered(DateTime.Now);
            if (b.view.Dozing || !Pool["woken"].Contains(b.bubbleText.Text)) bad("buddy: hovering should wake her (she said: " + b.bubbleText.Text + ")");
            b.view.Looking = false;
            if (b.ShouldDoze(DateTime.Now.AddSeconds(10), "sleepy")) bad("buddy: fell straight back asleep after you woke her");
            if (!b.ShouldDoze(DateTime.Now.AddSeconds(30), "sleepy")) bad("buddy: never went back to sleep after being woken");
            // Falling asleep, step by step: pictures of each stage, and checks on the book and her head
            var dozePics = new List<KeyValuePair<string, ImageSource>>();
            double zt = b.view.Now + 900, zClock = zt;
            Action<double> wait = secs => { int n = (int)(secs * 60); for (int i = 1; i <= n; i++) b.view.Step(zClock + i / 60.0); zClock += n / 60.0; };
            Action<string> snap = label => { dozePics.Add(new KeyValuePair<string, ImageSource>(label, b.Picture())); inWindow(label); };
            // Lying propped on her elbows, her hips rest on the floor too (they used to float 40 above her elbows)
            Action<string> resting = what =>
            {
                double hips = (b.view.ScreenPoint("bone003").Y + b.view.ScreenPoint("bone005").Y) / 2;
                double elbows = (b.view.ScreenPoint("bone034").Y + b.view.ScreenPoint("bone036").Y) / 2;
                // (floating was 45. How far below the floor line her lowest point shows isn't checked: her lowest
                // point is always put on the floor, and the hand nearer the camera just looks lower, by up to 20 more)
                if (Math.Abs(hips - elbows) > 30)
                    bad("buddy: " + what + ", she isn't resting on the floor (hips at " + hips.ToString("0") + ", elbows at " + elbows.ToString("0") + ")");
            };
            // how far the book is from between her hands (world units)
            Func<double> bookAway = () =>
            {
                var wl = b.view.WorldPoint("bone039"); var wr = b.view.WorldPoint("bone041");
                var mid = new Point3D((wl.X + wr.X) / 2, (wl.Y + wr.Y) / 2, b.view.BookAt.Z);
                return (b.view.BookAt - mid).Length;
            };
            b.view.Dozing = false; b.view.SnapTo("lie");
            b.view.Step(zt); wait(1);
            snap("reading"); resting("reading");
            var headReading = b.view.ScreenPoint("bone009");
            double bookReading = bookAway();
            b.view.Dozing = true;
            wait(1.2); snap("shutting the book");
            wait(1.8); snap("folding her arms");
            wait(5); snap("asleep"); resting("asleep");
            if (b.view.DozeAmount < 1) bad("buddy: after 8 seconds she should be fully asleep (" + b.view.DozeAmount.ToString("0.00") + ")");
            // morning: bedtime ends while she's asleep, and she wakes up once
            b.wasBedtime = false;
            if (b.MorningDue("sleepy")) bad("buddy: woke up for the morning in the middle of the night");
            if (!b.MorningDue("bored")) bad("buddy: didn't wake up when bedtime ended");
            if (b.MorningDue("bored")) bad("buddy: woke up for the morning twice");
            if (b.view.ScreenPoint("bone009").Y < headReading.Y + 3) bad("buddy: asleep, her head should be lower than when reading (from " + headReading.Y.ToString("0") + " to " + b.view.ScreenPoint("bone009").Y.ToString("0") + ")");
            if (bookReading > 45 || bookAway() < 50) bad("buddy: the book should be in her hands while reading and pushed aside asleep (" + bookReading.ToString("0") + ", then " + bookAway().ToString("0") + " from her hands)");
            b.view.Dozing = false;
            wait(0.8); snap("waking up");
            wait(1.2);
            if (b.view.DozeAmount > 0 || bookAway() > 45) bad("buddy: 2 seconds after waking she should be reading again (" + b.view.DozeAmount.ToString("0.00") + ", book " + bookAway().ToString("0") + " from her hands)");
            // sitting, she nods off: her head droops, and keeps drooping or jerking up, never back to normal
            b.view.SnapTo("sit"); wait(1);
            snap("sitting");
            // (the head joint is the pivot the head turns on, so a nod doesn't move it: watch the top of her head instead)
            double headSitting = b.view.Extent().Top;
            b.view.Dozing = true;
            wait(8); snap("nodding off");
            double nodLow = 0, nodHigh = double.MaxValue;
            for (int i = 0; i < 17 * 4; i++) { wait(0.25); double y = b.view.Extent().Top; nodLow = Math.Max(nodLow, y); nodHigh = Math.Min(nodHigh, y); }
            if (nodLow < headSitting + 6 || nodLow < nodHigh + 6) bad("buddy: nodding off, her head should droop and jerk back up (top of her head awake " + headSitting.ToString("0") + ", nodding between " + nodHigh.ToString("0") + " and " + nodLow.ToString("0") + ")");
            b.view.Dozing = false; wait(2);
            Sheet(dir, "buddy-doze.png", dozePics, 4);

            // Mood in her motion: restless, she taps a foot in short bursts; otherwise her feet stay still
            Func<bool, double> tapRange = tapping =>
            {
                b.view.Tapping = tapping;
                b.view.SnapTo("stand");
                double tp = Math.Ceiling((b.view.Now + 100) / 20) * 20, lo = 999, hi = -999; // (the start of a burst)
                for (int i = 0; i <= 120; i++) { b.view.Step(tp + i / 60.0); double a = b.view.Model["bone021"].Ry; lo = Math.Min(lo, a); hi = Math.Max(hi, a); }
                return hi - lo;
            };
            double tapped = tapRange(true), still = tapRange(false);
            if (tapped < 10 || still > 1) bad("buddy: restless she should tap a foot, and not otherwise (ankle moved " + tapped.ToString("0") + " tapping, " + still.ToString("0") + " not)");
            if (!(MoodEnergy("pleased") > MoodEnergy("bored") && MoodEnergy("bored") > MoodEnergy("sleepy"))) bad("buddy: pleased should be livelier than bored, and bored than sleepy");
            b.view.Energy = 1;
            // Lying, facing either way, reading or asleep: resting on the floor, not floating
            foreach (int sd in new[] { 1, -1 })
                foreach (bool asleep in new[] { false, true })
                {
                    b.view.NextSide = sd; b.view.Dozing = asleep; b.view.SnapTo("lie");
                    double st3 = b.view.Now + 8000; b.view.Step(st3); b.view.Step(st3);
                    resting((asleep ? "asleep" : "reading") + " facing " + (sd > 0 ? "right" : "left"));
                }
            b.view.Dozing = false;
            log.AppendLine("Buddy: bedtime checked (dozes at night, wakes when you hover, back to sleep after 20 s)");

            // The exam window: built (never shown) and drawn to a picture to look over
            var examWin = b.ExamWindow();
            var examCard = (FrameworkElement)examWin.Content;
            examCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            examCard.Arrange(new Rect(examCard.DesiredSize));
            examCard.UpdateLayout();
            var examShot = new RenderTargetBitmap((int)Math.Ceiling(examCard.ActualWidth), (int)Math.Ceiling(examCard.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            examShot.Render(examCard);
            Sheet(dir, "buddy-exam.png", new List<KeyValuePair<string, ImageSource>> { new KeyValuePair<string, ImageSource>("", examShot) }, 1);
            examWin.Close();

            // Missed questions: she only offers a review when there's one to do, the offer names the
            // real number, and a click does what her bubble says
            int reviews = 0, opens = 0, offered = 0;
            var quiet = new BuddyWindow(model, st, () => opens++) { Suppressed = true, Review = () => reviews++ };
            try
            {
                quiet.Missed = () => 0;
                for (int i = 0; i < 200; i++) { quiet.HoverLine(); if (quiet.offering) bad("buddy: offered a review with nothing missed"); }
                quiet.Missed = () => 7;
                for (int i = 0; i < 200; i++)
                {
                    string before = quiet.lastLine, l = quiet.HoverLine();
                    if (l == before) bad("buddy: said the same line twice in a row: " + l);
                    if (!quiet.offering) continue;
                    offered++;
                    if (!l.Contains("7") || words(l) > 6) bad("buddy: bad review offer: " + l);
                }
                if (offered < 50 || offered > 150) bad("buddy: should offer a review about half the time (" + offered + " of 200)");
                quiet.offering = true; quiet.Clicked();
                quiet.offering = false; quiet.Clicked();
                if (reviews != 1 || opens != 1) bad("buddy: a click should start the review only while she's offering it (" + reviews + " reviews, " + opens + " opens)");
            }
            finally { quiet.Close(); }
            log.AppendLine("Buddy: review offers checked (" + offered + " of 200 hovers)");

            // Her walk: pictures through one full stride, and how her body rises and falls. A natural walk
            // bobs smoothly; sudden changes in how fast she's rising or falling look mechanical.
            b.view.SnapTo("walk");
            double w0 = b.view.Now, cycle = 1 / HeatherView.StepHz;
            b.view.Step(w0);
            var stride = new List<KeyValuePair<string, ImageSource>>();
            for (int i = 0; i < 12; i++)
            {
                b.view.Step(w0 + cycle + i * cycle / 12);
                stride.Add(new KeyValuePair<string, ImageSource>("walk " + i + "/12", b.Picture()));
                inWindow("walking");
            }
            b.view.Jump = 1;
            b.view.Step(w0 + 2.3 * cycle);
            stride.Add(new KeyValuePair<string, ImageSource>("mid-jump", b.Picture()));
            b.view.Jump = 0;
            Sheet(dir, "buddy-walk.png", stride, 7);
            // Being carried: she hangs from whichever part you've got hold of
            var held = new List<KeyValuePair<string, ImageSource>>();
            var parts = new[] { "bone009", "bone006", "bone039", "bone041", "bone021", "bone027" };
            var names = new[] { "head", "chest", "her left hand", "her right hand", "her left foot", "her right foot" };
            for (int i = 0; i < parts.Length; i++)
            {
                b.view.SnapTo("stand");
                b.view.SwayTarget = 0;
                b.view.DebugSway = 0;
                b.view.Grabbed = parts[i];
                b.view.Held = true;
                double ht = b.view.Now + 200 * (i + 1);
                b.view.Step(ht);
                // swung hard one way then the other: no part of her may leave her window
                for (int k = 1; k <= 240; k++)
                {
                    b.view.SwayTarget = k < 80 ? 28 : k < 160 ? -28 : 0;
                    b.view.Step(ht + k / 60.0);
                    if (k % 4 == 0) inWindow("held by " + names[i] + ", swinging");
                }
                for (int k = 241; k <= 480; k++) b.view.Step(ht + k / 60.0); // time for the swing to settle
                // the held part must be above her middle once she's hanging from it
                var hold = b.view.ScreenPoint(parts[i]);
                var middle = b.view.ScreenPoint("bone003");
                if (parts[i] != "bone006" && hold.Y > middle.Y - 20) bad("buddy: held by " + names[i] + " she should hang below it (it's at " + hold.Y.ToString("0") + ", her hips at " + middle.Y.ToString("0") + ")");
                var top = b.view.ScreenPoint("bone009");
                if (top.Y < 0 || top.Y > H || hold.Y < 0 || hold.Y > H) bad("buddy: held by " + names[i] + " part of her is outside her window (head at " + top.Y.ToString("0") + ", held part at " + hold.Y.ToString("0") + " of " + H + ")");
                held.Add(new KeyValuePair<string, ImageSource>("held by " + names[i], b.Picture()));
                b.view.Held = false;
                b.view.Step(ht + 5);
                b.view.Step(ht + 6);
            }
            b.view.Grabbed = "bone009";
            Sheet(dir, "buddy-held.png", held, 3);
            log.AppendLine("Buddy: the furthest she reached in her " + W + "x" + H + " window: " + reach.Left.ToString("0") + "," + reach.Top.ToString("0") + " to " + reach.Right.ToString("0") + "," + reach.Bottom.ToString("0") + " (floor at " + GroundY + ")");

            // Getting up off the floor and lying back down: a picture every quarter second
            Action<string, string, string> moveSheet = (fromPose, toPose, file) =>
            {
                var frames = new List<KeyValuePair<string, ImageSource>>();
                b.view.NextSide = 1; b.view.Dozing = false; b.view.SnapTo(fromPose);
                double m0 = b.view.Now + 12000;
                b.view.Step(m0); b.view.Step(m0 + 0.5);
                b.view.Activity = toPose;
                // smoothness: the biggest change in any joint's angle from one frame (1/60 s) to the next
                var watched = new[] { "bone002", "bone003", "bone005", "bone006", "bone007", "bone008", "bone009", "bone021", "bone026", "bone027", "bone032", "bone034", "bone036", "bone039", "bone041" };
                Func<double[]> angles = () => watched.SelectMany(jn => { var jt = b.view.Model[jn]; return new[] { jt.Rx, jt.Ry, jt.Rz }; }).ToArray();
                var lastA = angles(); double jerk = 0; string jerkAt = "";
                for (int i = 0; i <= 40; i++)
                {
                    for (int k = 1; k <= 15; k++)
                    {
                        b.view.Step(m0 + 0.5 + (i * 15 + k) / 60.0);
                        var nowA = angles();
                        for (int q = 0; q < nowA.Length; q++)
                            if (Math.Abs(nowA[q] - lastA[q]) > jerk) { jerk = Math.Abs(nowA[q] - lastA[q]); jerkAt = watched[q / 3] + " at " + ((i * 15 + k) / 60.0).ToString("0.00") + " s"; }
                        lastA = nowA;
                    }
                    frames.Add(new KeyValuePair<string, ImageSource>((i + 1) * 0.25 + " s", b.Picture()));
                    inWindow(fromPose + " to " + toPose);
                    if (!b.view.Moving) break;
                }
                Sheet(dir, file, frames, 6);
                log.AppendLine("Buddy: " + fromPose + " to " + toPose + ", fastest joint change " + jerk.ToString("0.0") + " degrees in one frame (" + jerkAt + ")");
                if (jerk > 12) bad("buddy: " + fromPose + " to " + toPose + ", a joint snaps " + jerk.ToString("0") + " degrees in one frame (" + jerkAt + ")");
            };
            // Kneeling up on one knee and with one foot forward, what should touch the floor does
            // (the lowest bit of her near each of those joints is within a few units of her lowest bit overall)
            foreach (var kp in new[] { "halfkneel:bone027,bone007", "footfwd:bone027,bone007,bone039,bone041" })
            {
                string kpose = kp.Split(':')[0];
                b.view.NextSide = 1; b.view.SnapTo(kpose);
                double kt = b.view.Now + 14000; b.view.Step(kt); b.view.Step(kt);
                double kfloor = b.view.LowestNear(null, 0);
                inWindow(kpose + " (still)");
                foreach (var jn in kp.Split(':')[1].Split(','))
                {
                    double g = b.view.LowestNear(jn, jn == "bone007" ? 45 : 90) - kfloor; // (a hand counts its fingers, a foot its toes)
                    if (g > 20) bad("buddy: in the " + kpose + " pose, " + jn + " should touch the floor but is " + g.ToString("0") + " above it");
                }
            }
            moveSheet("lie", "stand", "buddy-getup.png");
            moveSheet("stand", "lie", "buddy-liedown.png");
            moveSheet("stand", "sit", "buddy-sitdown.png");
            moveSheet("sit", "stand", "buddy-situp.png");
            b.view.SnapTo("walk");
            b.view.Step(w0 + 2.9 * cycle);
            var heights = new List<double>();
            for (int i = 0; i <= 240; i++) { b.view.Step(w0 + 3 * cycle + i * cycle / 240); heights.Add(b.view.DebugLift); }
            double jolt = 0;
            for (int i = 2; i < heights.Count; i++) jolt = Math.Max(jolt, Math.Abs(heights[i] - 2 * heights[i - 1] + heights[i - 2]));
            log.AppendLine("Buddy: walking, her body bobs " + (heights.Max() - heights.Min()).ToString("0.0") + " units; sharpest jolt " + jolt.ToString("0.00") + " (smooth is under 0.05)");
            log.AppendLine("Buddy: wrists through a stride" + b.view.DebugArms());
            log.AppendLine("Buddy: leg angles through a stride (heel strike first, toe-off at step 14):" + b.view.DebugGait());
            log.AppendLine("Buddy: foot dips when the ankle tips -20..30 degrees: " + b.view.DebugFoot);
            log.AppendLine("Buddy: bob trace " + string.Join(" ", heights.Where((h, i) => i % 6 == 0).Select(h => (h - heights.Min()).ToString("0.0"))));

            // How long one frame of posing takes (it has to fit many times over in a 1/60 s frame to look smooth)
            foreach (var pose in new[] { "sit", "lie" })
            {
                b.view.SnapTo(pose);
                double t1 = b.view.Now;
                b.view.Step(t1);
                var timer = Stopwatch.StartNew();
                for (int i = 1; i <= 200; i++) b.view.Step(t1 + i / 60.0);
                double all = timer.Elapsed.TotalMilliseconds / 200;
                timer.Restart();
                for (int i = 0; i < 200; i++) b.view.Model.Update();
                double skin = timer.Elapsed.TotalMilliseconds / 200;
                log.AppendLine("Buddy: one frame of posing (" + pose + ") takes " + all.ToString("0.00") + " ms, of which moving her " + b.view.Model.VertexCount + " points " + skin.ToString("0.00") + " ms");
            }

            // Two monitors, laid out like the real ones: the second to the left and 2 units lower
            var real = Monitors;
            // (first, the real ones: the list must include the main monitor exactly as WPF reports it)
            var wa = SystemParameters.WorkArea;
            if (!real().Any(m => Math.Abs(m.Left - wa.Left) < 1 && Math.Abs(m.Top - wa.Top) < 1 && Math.Abs(m.Width - wa.Width) < 1 && Math.Abs(m.Height - wa.Height) < 1))
                bad("buddy: the real monitor list doesn't include the main monitor's working area");
            log.AppendLine("Buddy: sees " + real().Count + " real monitor(s): " + string.Join("  ", real().Select(m => m.Width + "x" + m.Height + " at " + m.Left + "," + m.Top)));
            try
            {
                Rect main = new Rect(0, 0, 1920, 1032), second = new Rect(-1920, 2, 1920, 1032);
                Monitors = () => new List<Rect> { main, second };
                Action<double, double, string> at = (got, want, what) => { if (Math.Abs(got - want) > 0.01) bad("buddy: " + what + " should be " + want + " but is " + got); };
                double nan = double.NaN;
                // her middle is x + W/2; she may go 80 units short of each outer edge, and freely across the join
                at(ClampX(-9000, nan) + W / 2, -1920 + 80, "the far left of two monitors");
                at(ClampX(9000, nan) + W / 2, 1920 - 80, "the far right of two monitors");
                at(ClampX(-W / 2 - 3, nan) + W / 2, -3, "a spot on the join between monitors");
                b.shelfY = nan;
                b.Left = 500 - W / 2; b.Top = 1032 - GroundY; b.Ground(); at(b.Top + GroundY, 1032, "her floor on the main monitor");
                b.Left = -500 - W / 2; b.Ground(); at(b.Top + GroundY, 1034, "her floor on the second monitor");
                // walks: mostly on her own monitor, now and then across, never off the floor
                int crossed = 0, walks = 0;
                for (int i = 0; i < 1000; i++)
                {
                    b.Left = 500 - W / 2; b.Top = 1032 - GroundY;
                    double target;
                    if (!b.PickSpot(out target)) continue;
                    walks++;
                    double mid = target + W / 2;
                    if (mid < -1920 + 80 - 0.01 || mid > 1920 - 80 + 0.01) bad("buddy: picked a walk off the end of the floor: " + mid);
                    if (mid < 0) crossed++;
                }
                if (walks < 900) bad("buddy: couldn't find somewhere to walk (" + walks + " of 1000)");
                if (crossed < 20 || crossed > 250) bad("buddy: should cross monitors now and then (" + crossed + " of " + walks + " walks)");
                // the second monitor is unplugged while she's on it: she comes back onto the one that's left
                Monitors = () => new List<Rect> { main };
                at(ClampX(-500 - W / 2, nan) + W / 2, 80, "her spot after the second monitor is unplugged");
                // a monitor stacked above the main one doesn't pull her up off the main taskbar
                Monitors = () => new List<Rect> { main, new Rect(0, -1080, 1920, 1032) };
                b.Left = 500 - W / 2; b.Top = 1032 - GroundY; b.Ground(); at(b.Top + GroundY, 1032, "her floor with a monitor stacked above");
                log.AppendLine("Buddy: two monitors checked (crossed on " + crossed + " of " + walks + " walks)");

                // Crossing to the other monitor she jumps the join; a walk that stays on one monitor never leaves the ground
                Monitors = () => new List<Rect> { main, second };
                Func<double, double, double> walkTo = (fromMid, toMid) =>
                {
                    b.StopWalking();
                    b.shelfY = nan; b.Left = fromMid - W / 2; b.Top = 1032 - GroundY; b.view.SnapTo("stand");
                    b.walkTarget = toMid - W / 2; b.afterWalk = "stand"; b.view.WalkDir = toMid > fromMid ? 1 : -1;
                    b.view.Activity = "walk"; b.walking = true;
                    double jt = b.view.Now, highest = 0;
                    b.view.Step(jt);
                    for (int i = 1; i <= 60 * 30 && b.walking; i++)
                    {
                        b.view.Step(jt + i / 60.0);
                        highest = Math.Max(highest, b.FloorAt(b.Left + W / 2) - (b.Top + GroundY));
                    }
                    if (b.walking) bad("buddy: a walk from " + fromMid + " to " + toMid + " never finished");
                    return highest;
                };
                double leap = walkTo(300, -600);
                if (leap < 55) bad("buddy: crossing to the other monitor she should jump the join (rose only " + leap.ToString("0") + ")");
                at(b.Top + GroundY, 1034, "her feet on the second monitor after the jump");
                if (Math.Abs(b.Left + W / 2 + 600) > 10) bad("buddy: after the jump she should end up at -600, but she's at " + (b.Left + W / 2).ToString("0"));
                double stroll = walkTo(300, 900);
                if (stroll > 0.5) bad("buddy: jumped during a walk that never left the monitor (rose " + stroll.ToString("0") + ")");
                log.AppendLine("Buddy: jumps the join between monitors (" + leap.ToString("0") + " units high)");

                // Putting her anywhere: let go mid-screen and that height becomes her floor
                Monitors = () => new List<Rect> { main, second };
                b.Carry(300 - W / 2, 500 - GroundY); b.PutDown();
                at(b.shelfY, 500, "her shelf after being put down mid-screen");
                at(b.Top + GroundY, 500, "her feet on that shelf");
                at(st.BuddyY, 500, "the saved shelf height");
                at(b.FloorAt(-500), 500, "the shelf's height over on the second monitor");
                at(ClampX(-9000, 500) + W / 2, -1920 + 80, "the far left of a shelf across two monitors");
                // a new Heather started from those saved numbers is back on the shelf
                var again = new BuddyWindow(model, st, () => { }) { Suppressed = true };
                at(again.Top + GroundY, 500, "her feet after a restart");
                again.Close();
                // carried too high or too low, she's kept where all of her shows
                b.Carry(300 - W / 2, 5 - GroundY); at(b.Top + GroundY, MinFloor, "her feet when carried to the very top of the screen");
                b.Carry(300 - W / 2, 5000 - GroundY); at(b.Top + GroundY, 1032, "her feet when carried below the taskbar");
                // let go near the taskbar: she lands on it
                b.Carry(300 - W / 2, 1032 - 25 - GroundY); b.PutDown();
                if (!double.IsNaN(b.shelfY)) bad("buddy: let go just above the taskbar, she should land on it");
                at(b.Top + GroundY, 1032, "her feet after landing on the taskbar");
                // the menu brings her down from a shelf (she drops; that takes a moment of made-up time)
                b.Carry(300 - W / 2, 600 - GroundY); b.PutDown(); b.BackToTaskbar();
                double dropT = b.view.Now;
                b.view.Step(dropT);
                for (int i = 1; i <= 480 && (b.falling || b.landing); i++) b.view.Step(dropT + i / 120.0);
                if (!double.IsNaN(b.shelfY) || !double.IsNaN(st.BuddyY)) bad("buddy: 'Back to the taskbar' left her on the shelf");
                at(b.Top + GroundY, 1032, "her feet after 'Back to the taskbar'");
                // let go hanging off the far right of the screens: she walks back on, never jumps
                b.Carry(1920 - 20 - W / 2, 1032 - GroundY); b.view.SnapTo("stand"); b.PickUp(null);
                b.PinGrab(new Point(1900, 1032 - 300)); b.LetGo(new Vector());
                double et = b.view.Now + 50, prevX = b.Left, biggest = 0;
                b.view.Step(et);
                for (int i = 1; i <= 120 * 12 && (b.landing || b.walking || i < 10); i++)
                {
                    b.view.Step(et + i / 120.0);
                    biggest = Math.Max(biggest, Math.Abs(b.Left - prevX));
                    prevX = b.Left;
                }
                if (biggest > 15) bad("buddy: let go off the edge of the screen, she jumped " + biggest.ToString("0") + " units in one frame");
                at(b.Left + W / 2, 1920 - 80, "her middle after walking back on from the edge of the screen");
                // a shelf doesn't run on into a monitor that isn't at that height
                Monitors = () => new List<Rect> { main, new Rect(-1280, 300, 1280, 700) };
                at(ClampX(-9000, 200) + W / 2, 80, "the left end of a shelf above a shorter neighbour");
                // her shelf's monitor goes away: back to the taskbar of the one that's left
                Monitors = () => new List<Rect> { main, new Rect(0, -1080, 1920, 1032) };
                b.Carry(300 - W / 2, -500 - GroundY); b.PutDown();
                at(b.shelfY, -500, "a shelf on the monitor above");
                Monitors = () => new List<Rect> { main };
                b.CheckShelf(); b.Left = b.Clamp(b.Left); b.Ground();
                if (!double.IsNaN(b.shelfY)) bad("buddy: kept a shelf on a monitor that's gone");
                at(b.Top + GroundY, 1032, "her feet after her shelf's monitor went away");
                log.AppendLine("Buddy: shelves checked (put down, restart, limits, landing, menu, lost monitor)");

                // (lets a fall play out in made-up time, 120 frames a second; gives back how many frames it took)
                Func<int> fly = () =>
                {
                    double ft = b.view.Now + 100;
                    b.view.Step(ft);
                    int n = 0;
                    while ((b.falling || b.landing) && n++ < 120 * 20) b.view.Step(ft + n / 120.0);
                    return n;
                };

                // Sitting on a window (made-up windows, listed front to back)
                Monitors = () => new List<Rect> { main, second };
                IntPtr win = new IntPtr(1), cover = new IntPtr(2);
                var frames = new Dictionary<IntPtr, Rect> { { win, new Rect(200, 300, 800, 500) } };
                var order = new List<IntPtr> { win };
                Windows = () => order.Where(frames.ContainsKey).Select(h => new KeyValuePair<IntPtr, Rect>(h, frames[h])).ToList();
                FrameOf = h => frames.ContainsKey(h) ? frames[h] : (Rect?)null;
                b.view.SnapTo("stand");
                b.Carry(500 - W / 2, 290 - GroundY); b.PutDown();
                if (b.perch != win) bad("buddy: put down on a window's top edge, she should be on that window");
                at(b.Top + GroundY, 300, "her feet on the window's top edge");
                if (b.view.Activity != "sit") bad("buddy: put on a window she should sit down, but she's going to " + b.view.Activity);
                if (!double.IsNaN(st.BuddyY)) bad("buddy: sitting on a window shouldn't be saved as a shelf");
                at(b.Clamp(-9000) + W / 2, 200 + 80, "the left end of the window she's on");
                at(b.Clamp(9000) + W / 2, 1000 - 80, "the right end of the window she's on");
                double rode = b.Left;
                frames[win] = new Rect(300, 250, 800, 500); b.RidePerch(0);
                at(b.Left, rode + 100, "her spot after the window moved 100 to the right");
                at(b.Top + GroundY, 250, "her feet after the window moved up");
                // dragged around fast, she throws her arms out for balance, and settles once it stops
                b.view.SnapTo("sit");
                double bt0 = b.view.Now + 50;
                b.view.Step(bt0);
                for (int i = 1; i <= 20; i++) { frames[win] = new Rect(300 - 15 * i, 250, 800, 500); b.RidePerch(1 / 60.0); b.view.Step(bt0 + i / 60.0); }
                if (b.view.BalanceAmount < 0.6 || b.view.BalanceDir != -1) bad("buddy: her window dragged left at 900 a second, she should throw her arms out (" + b.view.BalanceAmount.ToString("0.00") + ", leaning " + b.view.BalanceDir + ")");
                if (!Pool["ride"].Contains(b.bubbleText.Text)) bad("buddy: no comment on the bumpy ride (" + b.bubbleText.Text + ")");
                // both hands well out to the sides (how far each hand is beyond its own shoulder, sideways)
                {
                    var neckS = b.view.ScreenPoint("bone006");
                    Func<string, string, double> outBy = (shoulder, palm) => Math.Abs(b.view.ScreenPoint(palm).X - neckS.X) - Math.Abs(b.view.ScreenPoint(shoulder).X - neckS.X);
                    double outL = outBy("bone026", "bone039"), outR = outBy("bone032", "bone041");
                    if (outL < 40 || outR < 40) bad("buddy: balancing, her arms should be out to the sides (left hand " + outL.ToString("0") + " out, right " + outR.ToString("0") + ")");
                }
                var shaken = new List<KeyValuePair<string, ImageSource>> { new KeyValuePair<string, ImageSource>("window dragged left", b.Picture()) };
                for (int i = 21; i <= 200; i++) { b.RidePerch(1 / 60.0); b.view.Step(bt0 + i / 60.0); }
                if (b.view.BalanceAmount > 0.05) bad("buddy: 3 seconds after her window stopped she should be settled (" + b.view.BalanceAmount.ToString("0.00") + ")");
                for (int i = 201; i <= 260; i++) { frames[win] = new Rect(0 + 2 * (i - 200), 250, 800, 500); b.RidePerch(1 / 60.0); b.view.Step(bt0 + i / 60.0); }
                if (b.view.BalanceAmount > 0.05) bad("buddy: a window nudged along slowly shouldn't make her flail (" + b.view.BalanceAmount.ToString("0.00") + ")");
                shaken.Add(new KeyValuePair<string, ImageSource>("settled", b.Picture()));
                Sheet(dir, "buddy-balance.png", shaken, 2);
                frames.Remove(win); b.CheckPerch(); fly();
                if (b.perch != IntPtr.Zero) bad("buddy: still on a window that closed");
                at(b.Top + GroundY, 1032, "her feet after her window closed");
                // an edge hidden behind another window isn't somewhere to sit: she gets a plain shelf there
                frames[win] = new Rect(200, 300, 800, 500); frames[cover] = new Rect(400, 200, 300, 400); order.Insert(0, cover);
                b.Carry(500 - W / 2, 295 - GroundY); b.PutDown();
                if (b.perch != IntPtr.Zero) bad("buddy: sat on a window edge that's hidden behind another window");
                at(b.shelfY, 295, "her shelf where a window edge is hidden");
                // a window against the top of the screen is too high to sit on
                frames[cover] = new Rect(0, 0, 1920, 1032);
                b.Carry(500 - W / 2, 45 - GroundY); b.PutDown();
                if (b.perch != IntPtr.Zero) bad("buddy: sat on a window that fills the screen");
                order.Remove(cover); frames.Remove(cover);

                // Climbing: from the taskbar she finds the window, walks under it, climbs it and sits on top
                b.shelfY = nan; b.perch = IntPtr.Zero; b.Left = 1500 - W / 2; b.Top = 1032 - GroundY; b.view.SnapTo("stand");
                IntPtr found;
                double under;
                if (!b.FindClimb(out found, out under) || found != win) bad("buddy: didn't find the window to climb");
                at(under, 1000 - 70, "where she'd go up (the nearest spot under the window)");
                b.GoClimb(found, under);
                double ct = b.view.Now;
                b.view.Step(ct);
                int steps = 0;
                while (b.climbing != 0 && steps++ < 30 * 60) b.view.Step(ct + steps / 30.0);
                if (b.climbing != 0 || b.perch != win) bad("buddy: never got to the top of the window (" + steps / 30 + " s, feet at " + (b.Top + GroundY) + ")");
                at(b.Top + GroundY, 300, "her feet on top of the window after climbing");
                if (b.view.Activity != "sit") bad("buddy: after climbing she should sit, but she's going to " + b.view.Activity);
                int up = steps / 30;
                // ...and climbs back down to the taskbar
                while (b.view.Settled == null && steps++ < 60 * 60) b.view.Step(ct + steps / 30.0);
                b.ClimbDown();
                int start = steps;
                while (b.climbing != 0 && steps++ < start + 30 * 60) b.view.Step(ct + steps / 30.0);
                if (b.climbing != 0 || !double.IsNaN(b.shelfY)) bad("buddy: never got back down to the taskbar");
                at(b.Top + GroundY, 1032, "her feet after climbing down");
                // the window closes while she's on her way up: she turns round and comes down
                while (b.view.Settled == null && steps++ < 120 * 60) b.view.Step(ct + steps / 30.0);
                b.Left = 600 - W / 2; b.view.SnapTo("stand");
                b.GoClimb(win, 600);
                start = steps;
                while (b.Top + GroundY > 700 && steps++ < start + 30 * 60) b.view.Step(ct + steps / 30.0);
                frames.Remove(win);
                start = steps;
                while (b.climbing != 0 && steps++ < start + 30 * 60) b.view.Step(ct + steps / 30.0);
                if (b.climbing != 0 || !double.IsNaN(b.shelfY)) bad("buddy: stuck part-way up a window that closed");
                at(b.Top + GroundY, 1032, "her feet after the window she was climbing closed");
                log.AppendLine("Buddy: windows checked (sit, ride, close, hidden edge, climb up in " + up + " s, climb down, window closing mid-climb)");

                // Falling and throwing
                // her window closes under her: she drops to the taskbar, over several frames
                frames[win] = new Rect(200, 300, 800, 500);
                b.view.SnapTo("sit"); b.shelfY = nan; b.perch = IntPtr.Zero;
                b.Carry(500 - W / 2, 290 - GroundY); b.PutDown();
                frames.Remove(win); b.CheckPerch();
                if (!b.falling || !b.view.Held) bad("buddy: her window closed, she should be falling");
                double f0 = b.view.Now;
                b.view.Step(f0); b.view.Step(f0 + 0.05);
                if (b.Top + GroundY >= 1032 || b.Top + GroundY <= 300) bad("buddy: the fall should take time, but her feet went straight to " + (b.Top + GroundY));
                int fell = fly();
                if (b.falling || b.landing || b.view.Held) bad("buddy: never landed after her window closed");
                at(b.Top + GroundY, 1032, "her feet after falling from a closed window");
                // thrown up and to the right from mid-screen: she comes down on the taskbar, still on screen
                b.Carry(900 - W / 2, 600 - GroundY); b.PickUp(null); b.LetGo(new Vector(1400, -1500));
                if (!b.falling) bad("buddy: let go while moving fast, she should be thrown");
                fly();
                at(b.Top + GroundY, 1032, "her feet after being thrown");
                if (b.lastMoment != "dizzy" || !Pool["dizzy"].Contains(b.bubbleText.Text)) bad("buddy: thrown hard, she should land dizzy (did " + b.lastMoment + ", said " + b.bubbleText.Text + ")");
                b.lastMoment = null;
                if (b.Left + W / 2 < -1920 + 59 || b.Left + W / 2 > 1920 - 59) bad("buddy: thrown off the screen, she's at " + (b.Left + W / 2));
                if (b.Left + W / 2 < 1000) bad("buddy: thrown to the right, she should land to the right, but she's at " + (b.Left + W / 2));
                // thrown so she comes down over a window: she lands on top of it and sits
                frames[win] = new Rect(200, 500, 800, 400);
                b.Carry(500 - W / 2, 250 - GroundY); b.PickUp(null); b.LetGo(new Vector(0, 600));
                fly();
                if (b.perch != win) bad("buddy: dropped over a window, she should land on it (feet at " + (b.Top + GroundY) + ")");
                if (b.lastMoment == "dizzy") bad("buddy: dropped gently, she shouldn't be dizzy");
                at(b.Top + GroundY, 500, "her feet on the window she landed on");
                // 'Back to the taskbar' from there is a drop too
                b.BackToTaskbar(); fly();
                at(b.Top + GroundY, 1032, "her feet after 'Back to the taskbar'");
                frames.Remove(win);
                log.AppendLine("Buddy: falling and throwing checked (a closed window drops her in " + (fell / 120.0).ToString("0.00") + " s)");

                var climb = new List<KeyValuePair<string, ImageSource>>();
                b.view.SnapTo("climb");
                double c0 = b.view.Now;
                b.view.Step(c0);
                for (int i = 0; i < 6; i++) { b.view.Step(c0 + 5 + i * 1.25 / 6); climb.Add(new KeyValuePair<string, ImageSource>("climb " + i + "/6", b.Picture())); inWindow("climbing"); }
                Sheet(dir, "buddy-climb.png", climb, 6);
            }
            finally { Monitors = real; }
            // A little pressing in is real (a thigh against her stomach); more than 30 points is a limb through her
            foreach (var c in clips.OrderByDescending(c => c.Value.Key))
            {
                log.AppendLine("Buddy: limbs pressing into her body, " + c.Key + ": " + c.Value.Value);
                if (c.Value.Key > 30) bad("buddy: " + c.Key + ", a limb passes through her body (" + c.Value.Value + ")");
            }
        }
        finally
        {
            Windows = realWindows;
            FrameOf = realFrameOf;
            b.Close();
        }
    }

    // A picture of her in a pose, part-way through a moment (null = just the pose)
    ImageSource Shot(string pose, string moment)
    {
        view.SnapTo(pose);
        double dur = moment == null ? 0 : MomentSeconds[moment];
        view.React(moment, dur);
        double t = view.Now;
        view.Step(t);
        view.Step(t + (moment == "glance" ? 0.25 : 0.5) * dur);
        return Picture();
    }

    // A picture of her exactly as she's posed right now
    ImageSource Picture()
    {
        view.Measure(new Size(W, H));
        view.Arrange(new Rect(0, 0, W, H));
        view.UpdateLayout();
        var shot = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
        shot.Render(view);
        // a faint line where the floor (the taskbar's top edge) is, so floating or sinking shows
        var floor = new DrawingVisual();
        using (var dc = floor.RenderOpen()) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1), new Point(0, GroundY + 0.5), new Point(W, GroundY + 0.5));
        shot.Render(floor);
        return shot; // the whole window, so anything cut off by its edges shows
    }

    // Lay labelled pictures out in a grid and save it as one PNG
    static void Sheet(string dir, string file, List<KeyValuePair<string, ImageSource>> cells, int cols)
    {
        double cw = cells[0].Value.Width, ch = cells[0].Value.Height;
        int rows = (cells.Count + cols - 1) / cols;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x3A)), null, new Rect(0, 0, cols * cw, rows * ch));
            for (int i = 0; i < cells.Count; i++)
            {
                double x = (i % cols) * cw, y = (i / cols) * ch;
                dc.DrawImage(cells[i].Value, new Rect(x, y, cw, ch));
                dc.DrawText(new FormattedText(cells[i].Key, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 15, Brushes.White, 1.0), new Point(x + 6, y + 4));
            }
        }
        var sheet = new RenderTargetBitmap((int)(cols * cw), (int)(rows * ch), 96, 96, PixelFormats.Pbgra32);
        sheet.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(sheet));
        using (var f = File.Create(Path.Combine(dir, file))) enc.Save(f);
    }

    // ---------- Where she can stand ----------
    // Windows lays every monitor out on one big grid (the primary monitor's top-left corner is 0,0; a
    // monitor to its left has negative X). She can be on any of them and walk from one to the next.

    // Every monitor's working area (the monitor minus its taskbar), in WPF units. The self-test swaps
    // this for made-up monitors.
    // shortcut: assumes every monitor uses the same display scaling; give each its own scale if they ever differ.
    public static Func<List<Rect>> Monitors = () =>
    {
        double scale = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Width / SystemParameters.WorkArea.Width; // pixels per unit
        return System.Windows.Forms.Screen.AllScreens.Select(s => new Rect(
            s.WorkingArea.X / scale, s.WorkingArea.Y / scale, s.WorkingArea.Width / scale, s.WorkingArea.Height / scale)).ToList();
    };

    // The monitor a point is on, or the nearest one if it's off all of them
    static Rect MonitorAt(double x, double y)
    {
        return Monitors().OrderBy(m =>
        {
            double dx = Math.Max(0, Math.Max(m.Left - x, x - m.Right)), dy = Math.Max(0, Math.Max(m.Top - y, y - m.Bottom));
            return dx * dx + dy * dy;
        }).First();
    }

    // The stretch of floor under her middle (cx), from its left end to its right end: the monitors
    // at her height, joined into one stretch wherever they touch. shelf is the height of the floor
    // she's standing on, or NaN when she's on the taskbar (every monitor has one of those).
    static void Span(double cx, double shelf, out double left, out double right)
    {
        var spans = new List<double[]>();
        foreach (var m in Monitors().Where(m => double.IsNaN(shelf) || (shelf > m.Top && shelf <= m.Bottom)).OrderBy(m => m.Left))
        {
            var last = spans.Count > 0 ? spans[spans.Count - 1] : null;
            if (last != null && m.Left <= last[1] + 1) last[1] = Math.Max(last[1], m.Right);
            else spans.Add(new[] { m.Left, m.Right });
        }
        if (spans.Count == 0) { var m = MonitorAt(cx, shelf); spans.Add(new[] { m.Left, m.Right }); }
        var best = spans.OrderBy(s => Math.Max(0, Math.Max(s[0] - cx, cx - s[1]))).First();
        left = best[0];
        right = best[1];
    }

    // The nearest allowed spot for her window: her middle stays at least 80 units inside the ends of her floor
    static double ClampX(double x, double shelf)
    {
        double l, r;
        Span(x + W / 2, shelf, out l, out r);
        return Math.Max(l + 80, Math.Min(r - 80, x + W / 2)) - W / 2;
    }

    double shelfY = double.NaN; // the height of the floor she's on; NaN = the taskbar

    // The window she's sitting on top of (if any), and where its visible frame is, in WPF units
    IntPtr perch;
    Rect perchRect;

    // The floor she's on, from its left end to its right end: the top of her window, or the stretch
    // of monitors at her height
    void FloorEnds(double cx, out double l, out double r)
    {
        if (perch != IntPtr.Zero) { l = perchRect.Left; r = perchRect.Right; }
        else Span(cx, shelfY, out l, out r);
    }

    double Clamp(double x)
    {
        double l, r;
        FloorEnds(x + W / 2, out l, out r);
        double edge = Math.Min(80, (r - l) / 2); // (a narrow window leaves less room to keep back from its ends)
        return Math.Max(l + edge, Math.Min(r - edge, x + W / 2)) - W / 2;
    }

    // How high the floor is under her: her shelf, or the top of the taskbar on the monitor she's over
    // (the one at her current height, in case monitors are stacked)
    double FloorAt(double cx)
    {
        return double.IsNaN(shelfY) ? MonitorAt(cx, Top + GroundY - 1).Bottom : shelfY;
    }

    // Put her feet on the floor (or, part-way through a jump, that far above it)
    void Ground() { Top = FloorAt(Left + W / 2) - GroundY - (jumpT < 0 ? 0 : JumpHigh * 4 * jumpT * (1 - jumpT)); }

    // ---------- Jumping from one monitor to the next ----------
    // When a walk takes her across the join between two monitors she doesn't stroll through it: she
    // runs the last steps and jumps the gap.
    const double JumpSeconds = 0.55, JumpHigh = 70, JumpRun = 2.2; // how long, how high, and how much faster she travels in the air
    double jumpT = -1; // 0..1 through a jump; -1 = not jumping

    // Is the join to another monitor just ahead of her, with her walk going well past it?
    bool JoinAhead(int dir)
    {
        if (perch != IntPtr.Zero) return false;
        double cx = Left + W / 2, l, r;
        var m = MonitorAt(cx, Top + GroundY - 1);
        double join = dir > 0 ? m.Right : m.Left;
        FloorEnds(cx, out l, out r);
        bool another = dir > 0 ? join < r - 1 : join > l + 1; // her floor carries on past this monitor's edge
        return another && (join - cx) * dir >= 0 && (join - cx) * dir < 90 && (walkTarget + W / 2 - join) * dir > 100;
    }

    // ---------- Putting her anywhere ----------
    // Drag her up off the taskbar and let go. On the top edge of a window: she sits on that window,
    // and rides along if it moves. Anywhere else: her feet stay at that height, on an invisible
    // floor (a shelf). Near a taskbar: she lands on it.
    const double MinFloor = 40; // her floor can be this close to the top of a monitor (so there's always some of her to grab)
    const double SnapDip = 40;  // let go this close to a window's top or a taskbar and she lands on it

    // While she's being carried she goes where the mouse takes her, kept on a monitor
    void Carry(double left, double top)
    {
        double cx = left + W / 2, floor = top + GroundY;
        var m = MonitorAt(cx, floor);
        Left = Math.Max(m.Left, Math.Min(m.Right, cx)) - W / 2;
        Top = Math.Max(m.Top + MinFloor, Math.Min(m.Bottom, floor)) - GroundY;
    }

    void PutDown()
    {
        // (her feet come down where the window has them; kept off the very top of the screen)
        var on = MonitorAt(Left + W / 2, Top + GroundY);
        Top = Math.Max(on.Top + MinFloor, Math.Min(on.Bottom, Top + GroundY)) - GroundY;
        double cx = Left + W / 2, floor = Top + GroundY;
        var edge = EdgeAt(cx, floor, SnapDip);
        if (edge.HasValue && SitOn(edge.Value)) view.Activity = "sit";
        else
        {
            perch = IntPtr.Zero;
            shelfY = floor >= MonitorAt(cx, floor).Bottom - SnapDip ? double.NaN : floor;
        }
        Left = Clamp(Left);
        Ground();
        SavePlace();
    }

    // From the right-click menu
    void BackToTaskbar()
    {
        if (double.IsNaN(shelfY)) { Say("I'm already down here."); return; }
        StartFall(0, 0); // she hops off and drops
    }

    // A shelf only counts while there's a monitor at that height; otherwise she's back on the taskbar
    void CheckShelf()
    {
        if (double.IsNaN(shelfY)) return;
        var m = MonitorAt(Left + W / 2, shelfY);
        if (shelfY < m.Top + MinFloor || shelfY > m.Bottom) { shelfY = double.NaN; perch = IntPtr.Zero; }
    }

    // Remember where she is for next time (a window she's on won't be there after a restart, so
    // that's remembered as the taskbar)
    void SavePlace()
    {
        stats.BuddyX = Left;
        stats.BuddyY = perch != IntPtr.Zero || climbing != 0 ? double.NaN : shelfY;
        stats.Save();
    }

    // ---------- Sitting on windows ----------
    static Rect Dip(Rect pixels)
    {
        double scale = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Width / SystemParameters.WorkArea.Width; // pixels per unit
        return new Rect(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale);
    }

    // The ordinary app windows on screen, front to back, with their visible frames in WPF units; and
    // where one window is right now (null once it's closed, minimized or hidden). The self-test
    // swaps both for made-up windows.
    public static Func<List<KeyValuePair<IntPtr, Rect>>> Windows =
        () => Native.AppWindows().Select(w => new KeyValuePair<IntPtr, Rect>(w.Key, Dip(w.Value))).ToList();
    public static Func<IntPtr, Rect?> FrameOf = h => { var r = Native.FrameOf(h); return r.HasValue ? Dip(r.Value) : (Rect?)null; };

    // The window whose top edge is within reach of her feet at (x, floor): the frontmost such window,
    // unless a window in front of it covers that spot
    static KeyValuePair<IntPtr, Rect>? EdgeAt(double x, double floor, double reach)
    {
        foreach (var w in Windows())
        {
            var r = w.Value;
            if (x < r.Left || x > r.Right) continue;
            if (Math.Abs(r.Top - floor) <= reach) return w;
            if (floor > r.Top && floor < r.Bottom) return null;
        }
        return null;
    }

    // Make a window's top edge her floor. False if it's too close to the top of the screen to sit on.
    bool SitOn(KeyValuePair<IntPtr, Rect> window)
    {
        var r = window.Value;
        if (r.Top < MonitorAt(Left + W / 2, r.Top).Top + MinFloor) return false;
        perch = window.Key;
        perchRect = r;
        shelfY = r.Top;
        return true;
    }

    // Every frame while she's on a window: if it has moved, she rides along. Dragged around fast, she
    // throws her arms out to keep her balance (dt: seconds since the last frame).
    void RidePerch(double dt)
    {
        view.Balance = 0;
        if (perch == IntPtr.Zero || dragging) return;
        var r = FrameOf(perch);
        if (!r.HasValue || r.Value == perchRect) return;
        double dx = r.Value.Left - perchRect.Left, dy = r.Value.Top - perchRect.Top;
        if (dt > 0)
        {
            double speed = Math.Sqrt(dx * dx + dy * dy) / Math.Max(dt, 1.0 / 240);
            view.Balance = Math.Min(1, Math.Max(0, (speed - 150) / 650)); // a slow nudge isn't a jolt
            if (Math.Abs(dx) > 0.5) view.BalanceDir = dx > 0 ? 1 : -1;
            if (view.Balance > 0.6 && DateTime.Now > nextRideLine) { nextRideLine = DateTime.Now.AddSeconds(20); Say(Line("ride", Mood)); }
        }
        Left += dx;
        perchRect = r.Value;
        shelfY = r.Value.Top;
        Left = Clamp(Left);
        Ground();
    }

    DateTime nextRideLine; // she only comments on a bumpy ride every 20 seconds at most

    // Once a second: is her window still there, and still low enough to sit on? If it closed, was
    // minimized or filled the screen, she's back on the taskbar.
    void CheckPerch()
    {
        if (perch == IntPtr.Zero) return;
        var r = FrameOf(perch);
        if (r.HasValue && r.Value.Top >= MonitorAt(Left + W / 2, r.Value.Top).Top + MinFloor) return;
        Say(Line("fall", Mood));
        StartFall(0, 0); // nothing under her any more
    }

    // ---------- Climbing ----------
    // Now and then, from the taskbar, she walks under an open window, climbs up its face and sits on
    // top. Later she may climb back down.
    const double ClimbSpeed = 70; // units a second
    int climbing;                 // -1 = going up, +1 = coming down, 0 = not climbing
    IntPtr climbTo;               // the window she's climbing up to

    // A window she can climb: any app window whose top is well above her and in the open where she'd
    // go up (x). Only one that touches the very top of the screen (a maximized one) is out, because
    // there'd be nowhere on screen for her to sit.
    bool FindClimb(out IntPtr window, out double x)
    {
        double cx = Left + W / 2, floor = Top + GroundY, l, r;
        FloorEnds(cx, out l, out r);
        var m = MonitorAt(cx, floor - 1);
        foreach (var w in Windows().OrderBy(w => rng.Next()))
        {
            var f = w.Value;
            if (f.Top < m.Top + MinFloor || f.Top > floor - 150 || f.Width < 200) continue;
            double lo = Math.Max(f.Left + 70, l + 80), hi = Math.Min(f.Right - 70, r - 80);
            if (lo > hi) continue;
            x = Math.Max(lo, Math.Min(hi, cx)); // the nearest spot under it
            var edge = EdgeAt(x, f.Top, 1);
            if (edge.HasValue && edge.Value.Key == w.Key) { window = w.Key; return true; }
        }
        window = IntPtr.Zero;
        x = 0;
        return false;
    }

    // Walk to x, then up (WalkStep turns the walk into a climb when she gets there)
    void GoClimb(IntPtr window, double x)
    {
        climbTo = window;
        climbing = -1;
        nextChange = DateTime.Now.AddMinutes(3); // nothing else until she's there
        walkTarget = Clamp(x - W / 2);
        view.WalkDir = walkTarget > Left ? 1 : -1;
        afterWalk = "climb";
        if (Math.Abs(walkTarget - Left) < 30) view.Activity = "climb";
        else { view.Activity = "walk"; walking = true; }
    }

    void ClimbDown()
    {
        perch = IntPtr.Zero;
        climbing = 1;
        nextChange = DateTime.Now.AddMinutes(3);
        view.Activity = "climb";
    }

    // Every frame: once she's in her climbing pose, move her up (or down) until she arrives
    void ClimbStep(double dt)
    {
        if (climbing == 0 || view.Settled != "climb") return;
        double cx = Left + W / 2, floor = Top + GroundY, goal;
        var frame = climbing < 0 ? FrameOf(climbTo) : null;
        if (climbing < 0 && !frame.HasValue) { climbing = 1; return; } // the window went away: back down
        goal = climbing < 0 ? frame.Value.Top : MonitorAt(cx, floor - 1).Bottom;
        double step = ClimbSpeed * Math.Min(dt, 0.1);
        if (Math.Abs(goal - floor) > step)
        {
            shelfY = floor + Math.Sign(goal - floor) * step;
            Ground();
            return;
        }
        // She's there
        if (climbing < 0)
        {
            if (!SitOn(new KeyValuePair<IntPtr, Rect>(climbTo, frame.Value))) { climbing = 1; return; }
            view.Activity = "sit";
            if (rng.Next(2) == 0) Say(Line("perch", Mood));
        }
        else
        {
            shelfY = double.NaN;
            view.Activity = PickPose(Mood);
        }
        climbing = 0;
        Left = Clamp(Left);
        Ground();
        SavePlace();
        nextChange = DateTime.Now.AddSeconds(40 + rng.Next(40));
    }

    // Which way she should lie so her head stays on screen: toward the middle when she's near an end of her floor
    int SideAt(double left, int preferred)
    {
        double cx = left + W / 2, l, r;
        FloorEnds(cx, out l, out r);
        if (cx > r - 240) return -1;
        if (cx < l + 240) return 1;
        return preferred;
    }

    void DoSomething()
    {
        string mood = Mood;
        bool sleepy = mood == "sleepy";
        // Sleepy: she stays put about twice as long
        nextChange = DateTime.Now.AddSeconds(sleepy ? 60 + rng.Next(60) : 30 + rng.Next(40));
        nextMoment = DateTime.Now.AddSeconds(30 + rng.Next(25)); // let her settle in before the next small moment
        string now = view.Settled ?? view.Activity;
        // Bedtime: once she's lying down she stays down for the night
        if (sleepy && now == "lie") { nextChange = DateTime.Now.AddMinutes(3 + rng.Next(3)); return; }
        string next = PickPose(mood);

        // Sometimes, from the taskbar, she goes and climbs a window instead (never when she's sleepy);
        // and from a window she sometimes climbs back down
        IntPtr window;
        double under;
        if (!sleepy && perch == IntPtr.Zero && double.IsNaN(shelfY) && rng.Next(100) < 15 && FindClimb(out window, out under))
        {
            GoClimb(window, under);
            return;
        }
        if (perch != IntPtr.Zero && rng.Next(100) < 25) { ClimbDown(); return; }

        // Now and then she wanders over to wherever your mouse is, and stands there
        var mouse = MouseFromHead();
        if (!sleepy && mouse.HasValue && rng.Next(100) < 12)
        {
            double spot = Clamp(Left + mouse.Value.X);
            if (Math.Abs(spot - Left) > 200) { WalkTo(spot, "stand"); return; }
        }

        // Most of the time she gets up and walks somewhere else first, then settles in there
        // (less when she's sleepy, more when she's restless)
        double target;
        if (rng.NextDouble() < (sleepy ? 0.3 : mood == "restless" ? 0.85 : 0.7) && PickSpot(out target))
        {
            WalkTo(target, next);
            return;
        }
        if (next == now)
        {
            if (sleepy && now == "lie") return; // too tired to move
            next = now == "lie" ? "sit" : "lie";
        }
        if (next == "lie") view.NextSide = SideAt(Left, rng.Next(2) == 0 ? 1 : -1);
        view.Activity = next;
    }

    // Set off walking to a spot, and settle into a pose when she gets there
    void WalkTo(double target, string then)
    {
        walkTarget = target;
        afterWalk = then;
        view.WalkDir = target > Left ? 1 : -1;
        view.NextSide = SideAt(target, view.WalkDir);
        view.Activity = "walk";
        walking = true;
    }

    // A random spot on her floor that's a real walk away. Four walks in five she stays on the monitor
    // she's on; the fifth can take her anywhere along the floor, across to another monitor.
    bool PickSpot(out double target)
    {
        double cx = Left + W / 2, l, r;
        FloorEnds(cx, out l, out r);
        if (rng.Next(5) > 0)
        {
            var m = MonitorAt(cx, Top + GroundY - 1);
            l = Math.Max(l, m.Left);
            r = Math.Min(r, m.Right);
        }
        target = Left;
        for (int i = 0; i < 12; i++)
        {
            double t = Clamp(l + rng.NextDouble() * (r - l) - W / 2);
            if (Math.Abs(t - Left) >= 220) { target = t; return true; }
        }
        return false;
    }

    // Moves the window with her steps (called every frame): speeds up as she starts and slows as she stops
    void WalkStep(double dt)
    {
        if (!walking) return;
        double speed = view.WalkSpeedDip;
        // Start stopping a little before the spot so she ends up right on it
        if (view.Activity == "walk" && Math.Abs(walkTarget - Left) <= speed * 0.3) view.Activity = afterWalk;
        double step = speed * view.WalkBlend * Math.Min(dt, 0.1);
        if (jumpT < 0 && step > 0 && JoinAhead(view.WalkDir)) jumpT = 0;
        if (jumpT >= 0)
        {
            // In the air: time moves the jump along, she covers ground faster, and Ground() lifts her along an arc
            jumpT += Math.Min(dt, 0.1) / JumpSeconds;
            step *= JumpRun;
            if (jumpT >= 1) jumpT = -1;
            view.Jump = jumpT < 0 ? 0 : Math.Sin(Math.PI * jumpT);
            if (step <= 0) Ground();
        }
        if (step > 0)
        {
            int dir = view.WalkDir;
            double next = Left + dir * step;
            if ((walkTarget - next) * dir <= 0) next = walkTarget;
            // (still past the edge, walking back on: the walk brings her in, not a jump)
            Left = Math.Abs(Clamp(Left) - Left) > 1 ? next : Clamp(next);
            Ground(); // the next monitor's taskbar may sit a little higher or lower
        }
        if (view.Activity != "walk" && view.WalkBlend <= 0 && !pausedWalk)
        {
            walking = false;
            SavePlace();
        }
    }

    void StopWalking()
    {
        walking = false;
        pausedWalk = false;
        jumpT = -1;
        view.Jump = 0;
        if (view.Activity == "walk") view.Activity = "stand";
        if (climbing != 0)
        {
            // Interrupted part-way up or down a window: she's simply back on the taskbar
            climbing = 0;
            shelfY = double.NaN;
            if (view.Activity == "climb") view.Activity = "stand";
        }
    }

    public void Refresh()
    {
        bool show = !Suppressed && !Disabled && !fullscreenApp && !IsSnoozed;
        if (show && !IsVisible) Show();
        else if (!show && IsVisible) { StopWalking(); Hide(); }
    }

    // Take her off the taskbar for a while (minutes < 0: until she's brought back from the tray menu)
    public void Snooze(int minutes)
    {
        StopWalking();
        ShowBubble(minutes < 0 ? "Okay. Call me when you need me." : "Okay. I'll be back in a bit.", true);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        t.Tick += (s, e) =>
        {
            t.Stop();
            snoozeUntil = minutes < 0 ? DateTime.MaxValue : DateTime.Now.AddMinutes(minutes);
            wasSnoozed = true;
            Refresh();
            if (Snoozed != null) Snoozed(minutes < 0 ? "until you bring her back" : minutes >= 60 ? "for an hour" : "for " + minutes + " minutes");
        };
        t.Start();
    }

    public void Unsnooze()
    {
        if (!IsSnoozed) return;
        snoozeUntil = DateTime.MinValue;
        wasSnoozed = false;
        Refresh();
        if (IsVisible) ComeBack(true);
    }

    // Right-click menu, styled to match the quiz
    ContextMenu BuildMenu()
    {
        ResourceDictionary res = null;
        try
        {
            res = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(MenuXaml);
        }
        catch { }
        var menu = new ContextMenu();
        Style item = null, sep = null;
        if (res != null)
        {
            menu.Style = (Style)res["menu"];
            item = (Style)res["item"];
            sep = (Style)res["sep"];
        }
        Action<string, Action> add = (text, act) =>
        {
            var mi = new MenuItem { Header = text };
            if (item != null) mi.Style = item;
            mi.Click += (s, e) => act();
            menu.Items.Add(mi);
        };
        add("Open quiz", onClick);
        add("Redo what I missed", ReviewFromMenu);
        add("Back to the taskbar", BackToTaskbar);
        add("Set exam date...", AskExamDate);
        var line = new Separator();
        if (sep != null) line.Style = sep;
        menu.Items.Add(line);
        add("Hide her for 15 minutes", () => Snooze(15));
        add("Hide her for 1 hour", () => Snooze(60));
        add("Hide her until I bring her back", () => Snooze(-1));
        return menu;
    }

    const string MenuXaml =
        "<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "<Style x:Key='menu' TargetType='ContextMenu'><Setter Property='OverridesDefaultStyle' Value='True'/><Setter Property='HasDropShadow' Value='False'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ContextMenu'>" +
        "<Border Background='#F2181110' BorderBrush='#66C23B30' BorderThickness='1' CornerRadius='10' Padding='5' Margin='0,0,8,8'>" +
        "<Border.Effect><DropShadowEffect BlurRadius='14' ShadowDepth='3' Opacity='0.55'/></Border.Effect>" +
        "<StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/></Border>" +
        "</ControlTemplate></Setter.Value></Setter></Style>" +
        "<Style x:Key='item' TargetType='MenuItem'><Setter Property='OverridesDefaultStyle' Value='True'/><Setter Property='Foreground' Value='#EAE3D6'/>" +
        "<Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/><Setter Property='FontSize' Value='13'/><Setter Property='Cursor' Value='Hand'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>" +
        "<Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='14,7,22,8'><ContentPresenter ContentSource='Header' RecognizesAccessKey='False'/></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='#48C23B30'/></Trigger></ControlTemplate.Triggers>" +
        "</ControlTemplate></Setter.Value></Setter></Style>" +
        "<Style x:Key='sep' TargetType='Separator'><Setter Property='OverridesDefaultStyle' Value='True'/><Setter Property='Template'><Setter.Value>" +
        "<ControlTemplate TargetType='Separator'><Border Height='1' Margin='10,4' Background='#26FFFFFF'/></ControlTemplate></Setter.Value></Setter></Style>" +
        "</ResourceDictionary>";

    // Show a bubble just above her head, wherever her head is right now
    void ShowBubble(string text, bool timed)
    {
        bubbleText.Text = text;
        PlaceBubble();
        if (!timed)
        {
            bubble.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            return;
        }
        var a = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3.5) };
        a.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        a.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));
        a.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.5))));
        bubble.BeginAnimation(OpacityProperty, a);
    }

    public void Say(string text) { ShowBubble(text, true); saidAt = DateTime.Now; }

    DateTime saidAt, hoverAt, leftAt; // when she last said something, showed a hover line, and the mouse left her
    DateTime wokeAt, nudgedOn;        // when you last woke her; the day she last mentioned your usual study time

    // Bedtime: lying down (or sitting: she nods off) late at night with nobody bothering her, and not just woken, she dozes
    bool ShouldDoze(DateTime now, string mood)
    {
        return mood == "sleepy" && (view.Settled == "lie" || view.Settled == "sit") && !view.Looking && !view.Watching && !dragging && (now - wokeAt).TotalSeconds > 20;
    }

    // How lively her idle motion is in each mood (1 = normal)
    static double MoodEnergy(string mood)
    {
        return mood == "pleased" ? 1.35 : mood == "restless" ? 1.2 : mood == "sleepy" ? 0.6 : 0.85;
    }

    // Bedtime just ended while she was asleep: time to wake up (true once, on that tick)
    bool wasBedtime;
    bool MorningDue(string mood)
    {
        bool bedtime = mood == "sleepy", due = wasBedtime && !bedtime && view.DozeAmount > 0.5;
        wasBedtime = bedtime;
        return due;
    }

    // Once a day, with no quiz in the last 3 hours (and not at bedtime), she gives you a nudge: at
    // your usual study time, or at any time in the week before your exam
    bool NudgeDue(DateTime now)
    {
        if (nudgedOn == now.Date || MoodFor(now, stats) == "sleepy" || (now - stats.LastQuiz).TotalHours < 3) return false;
        var days = ExamDays(now, stats);
        if (!StudyTime(now, stats) && !(days.HasValue && days <= 7)) return false;
        nudgedOn = now.Date;
        return true;
    }

    // The nudge. With questions you've missed, she offers to redo them, and a click on her then starts
    // that review (the quiz picks the exam's class first, that week). Otherwise a click opens the quiz.
    void Nudge(DateTime now)
    {
        int missed = Missed != null ? Missed() : 0;
        var days = ExamDays(now, stats);
        string name = string.IsNullOrWhiteSpace(stats.ExamName) ? "Exam" : stats.ExamName.Trim();
        string line;
        if (days.HasValue && days <= 7)
            line = (days == 0 ? name + " today." : days == 1 ? name + " tomorrow." : name + " in " + days + " days.") + (missed > 0 ? " Redo misses?" : " Quiz?");
        else
            line = missed > 0 ? "Your usual hour. Redo misses?" : Line("habit", Mood);
        Say(line);
        lastLine = line;
        offering = missed > 0;
        Act(new[] { "point", "glance" }.FirstOrDefault(m => HeatherView.Fits(m, view.Settled)));
        nextMoment = now.AddSeconds(25 + rng.Next(20));
    }

    // Is something she said (not a hover line) still showing? (Say shows it for 3.5 seconds)
    bool Speaking(DateTime now) { return (now - saidAt).TotalSeconds < 3.5; }

    // The mouse arrived over her: she looks, stops walking, and says a hover line, unless she's
    // still saying something or said a hover line moments ago (rubbing her passes over her again and
    // again, and each pass would otherwise swap the line)
    void Hovered(DateTime now)
    {
        view.Looking = true;
        if (view.Activity == "walk") { pausedWalk = true; view.Activity = "stand"; }
        if (view.Dozing)
        {
            // you woke her: she looks up, and stays awake a little while
            view.Dozing = false;
            wokeAt = now;
            hoverAt = now;
            Say(Line("woken", Mood));
            return;
        }
        if (Speaking(now) || (now - hoverAt).TotalSeconds < 3) return;
        hoverAt = now;
        ShowBubble(HoverLine(), false);
    }

    // Just above her head (called while the bubble shows, so it follows her if she moves)
    void PlaceBubble()
    {
        bubble.Margin = new Thickness(0); // measuring includes the margin, so clear the old position first
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = bubble.DesiredSize.Width, bh = bubble.DesiredSize.Height;
        var head = view.HeadPoint();
        if (double.IsNaN(head.X) || double.IsNaN(head.Y)) head = new Point(W / 2, GroundY - 300);
        // (kept on screen even when she's so high up that her head is off the top)
        screenTop = MonitorAt(Left + W / 2, Top + GroundY - 1).Top;
        double top = Math.Max(2, screenTop - Top + 2);
        bubble.Margin = new Thickness(Math.Max(4, Math.Min(W - bw - 4, head.X - bw / 2)), Math.Max(top, head.Y - 78 - bh), 0, 0);
    }

    double screenTop; // the top of the monitor she was on when the bubble appeared

    // Each frame while the bubble shows: move it only if her head has moved, using the size it
    // already has (no re-measuring)
    void FollowBubble()
    {
        double bw = bubble.ActualWidth, bh = bubble.ActualHeight;
        if (bw <= 0) return;
        var head = view.HeadPoint();
        if (double.IsNaN(head.X) || double.IsNaN(head.Y)) return;
        double top = Math.Max(2, screenTop - Top + 2);
        double x = Math.Max(4, Math.Min(W - bw - 4, head.X - bw / 2)), y = Math.Max(top, head.Y - 78 - bh);
        if (Math.Abs(x - bubble.Margin.Left) > 0.5 || Math.Abs(y - bubble.Margin.Top) > 0.5) bubble.Margin = new Thickness(x, y, 0, 0);
    }
}

static class Native
{
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    delegate bool EnumProc(IntPtr hWnd, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc each, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int what, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int what, out RECT value, int size);

    // Where a window's visible frame is, in pixels (null if it's closed, hidden or minimized).
    // Attribute 9 is the frame you can see; GetWindowRect also counts an invisible resize border.
    public static Rect? FrameOf(IntPtr h)
    {
        if (!IsWindowVisible(h) || IsIconic(h)) return null;
        RECT r;
        if (DwmGetWindowAttribute(h, 9, out r, 16) != 0 && !GetWindowRect(h, out r)) return null;
        return new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));
    }

    // The ordinary app windows on screen, front to back (the order Windows stacks them), in pixels
    public static List<KeyValuePair<IntPtr, Rect>> AppWindows()
    {
        var list = new List<KeyValuePair<IntPtr, Rect>>();
        uint me = (uint)Process.GetCurrentProcess().Id;
        EnumWindows((h, l) =>
        {
            uint pid;
            int cloaked;
            GetWindowThreadProcessId(h, out pid);
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            // Not ours, not a tool window or click-through overlay, not an untitled helper,
            // not a store app that's "running" but hidden (cloaked, attribute 14), not the desktop or a taskbar
            if (pid == me || (ex & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) != 0 || GetWindowTextLength(h) == 0) return true;
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c.StartsWith("Shell_")) return true;
            var r = FrameOf(h);
            if (r.HasValue && r.Value.Width >= 160 && r.Value.Height >= 80) list.Add(new KeyValuePair<IntPtr, Rect>(h, r.Value));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    struct LASTINPUTINFO { public uint cbSize, dwTime; }

    // Seconds since the keyboard or mouse was last used
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = 8 };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    struct RECT { public int Left, Top, Right, Bottom; }

    public static bool ForegroundIsFullscreen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        uint pid;
        GetWindowThreadProcessId(fg, out pid);
        if (pid == (uint)Process.GetCurrentProcess().Id) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, 64);
        string c = cls.ToString();
        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
        RECT r;
        if (!GetWindowRect(fg, out r)) return false;
        var screen = System.Windows.Forms.Screen.FromHandle(fg).Bounds;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }
}
