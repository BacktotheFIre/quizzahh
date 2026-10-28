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
            var pts = new Point3DCollection(n);
            var nrms = new Vector3DCollection(n);
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
                pts.Add(new Point3D(ox, oy, oz));
                nrms.Add(new Vector3D(onx, ony, onz));
            }
            sm.Geo.Positions = pts;
            sm.Geo.Normals = nrms;
        }
    }

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

    static readonly string[] Skirt = { "bone012", "bone013", "bone016", "bone017" };

    // ---------- Taskbar mode ----------
    // She changes pose through real in-between poses:
    //   lie <-> allfours (hands and knees) <-> kneel <-> stand <-> crouch <-> sit (on the edge)
    //   stand <-> walk
    // Each move is eased between the pose she's in and the next one, so getting up, kneeling,
    // turning and sitting down all look like movement instead of a cross-fade.
    public int WalkDir = 1;
    public double Fps = 30; // frame rate while settled (taskbar Heather goes to full frame rate while moving)
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
        new[] { "lie", "flat", "0.7" }, new[] { "flat", "allfours", "1.0" }, new[] { "allfours", "kneel", "0.9" }, new[] { "kneel", "stand", "1.0" },
        new[] { "stand", "crouch", "0.75" }, new[] { "crouch", "sit", "0.75" }, new[] { "stand", "walk", "0.5" }
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
            return 0;
        }
        if (IsFloor(a)) return FloorYaw * side;
        if (a == "walk") return 90 * WalkDir;
        if (a == "stand" && path.Count > 0)
        {
            string n = path.Peek();
            if (n == "walk") return 90 * WalkDir;
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
        if (lying) tg.Children.Add(new RotateTransform3D(pitchRot, new Point3D(0, hipY, hipZ)));
        tg.Children.Add(new RotateTransform3D(turn, new Point3D(0, 0, 0)));
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

    double Now { get { return clock.Elapsed.TotalSeconds; } }
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
        double t = Now;
        // 30 fps while she's settled keeps CPU low; full frame rate while she's moving between poses
        double fps = lying && Moving ? 60 : Fps;
        if (t - lastFrame < 1.0 / fps - 0.002) return;
        double dt = lastFrame < 0 ? 0 : Math.Min(0.1, t - lastFrame);
        lastFrame = t;
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
        switch (p)
        {
            case "think":
                Tgt("bone026", 0, -55, 30); Tgt("bone034", 150, 0, 0);
                Tgt("bone032", 0, -20, -45); Tgt("bone036", -100, 0, 0);
                hp = 8; hy = 10; hr = 6;
                break;
            case "cross":
                Tgt("bone026", 30, -30, 45); Tgt("bone034", 115, 0, 0);
                Tgt("bone032", 30, -30, -45); Tgt("bone036", -115, 0, 0);
                hr = -4;
                break;
            case "hips":
                Tgt("bone026", 0, 30, 20); Tgt("bone034", 100, 0, 0);
                Tgt("bone032", 0, 30, -20); Tgt("bone036", -100, 0, 0);
                hr = 5;
                break;
            case "facepalm":
                Tgt("bone026", 0, -85, 15); Tgt("bone034", 165, 0, 0);
                hp = -25; hy = -10;
                break;
            case "point":
                Tgt("bone026", 0, -30, -60); Tgt("bone034", 5, 0, 0);
                hy = 25;
                break;
            case "fist":
                Tgt("bone026", 0, -30, 40); Tgt("bone034", 130, 0, 0); Wig("bone034", 15 * Math.Sin(t * 12), 0, 0);
                hp = 6;
                break;
            case "cheer":
                Tgt("bone026", 0, 0, -57); Tgt("bone034", 72, 0, 0);
                Tgt("bone032", 0, 0, 57); Tgt("bone036", -72, 0, 0);
                hp = 10;
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

    // ---------- Taskbar engine ----------
    int side = 1;               // which way she faces on the floor: +1 = head toward the right of the screen
    public int NextSide = 1;    // the side she'll use the next time she gets down on the floor
    double lookMid, lookAmt, kickPh, swingPh, walkPh, walkBlend, segEndAt;
    bool segIn, segOut, endedMoving;

    // The body part that stays planted during a move: she moves over her feet when kneeling down,
    // over her knees when leaning onto her hands, and over her chest when stretching out to lie down.
    // driftX is how far that has carried her body sideways (it returns to ~0 when she gets back up).
    string[] anchor;
    double anchorStart, driftX;
    static readonly string[] Ankles = { "bone021", "bone027" }, Knees = { "bone007", "bone008" }, Chest = { "bone006" };

    static bool Pair(string a, string b, string x, string y) { return (a == x && b == y) || (a == y && b == x); }

    static string[] AnchorFor(string a, string b)
    {
        if (Pair(a, b, "stand", "kneel") || Pair(a, b, "stand", "crouch")) return Ankles;
        if (Pair(a, b, "kneel", "allfours")) return Knees;
        if (Pair(a, b, "allfours", "flat")) return Chest;
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
    public bool Moving { get { return segTo != null || path.Count > 0 || state == "walk"; } }
    // How fast to move the window so her steps match the ground (DIPs per second)
    public double WalkSpeedDip { get { return walkUnitsPerSec / camWpd; } }
    double walkUnitsPerSec = 300;

    static bool IsFloor(string s) { return s == "kneel" || s == "allfours" || s == "flat" || s == "lie"; }

    // ----- The stride -----
    // Gait phase u runs 0 to 1: heel strike at 0, the foot is planted until StanceEnd, then it swings.
    // The foot's path is chosen first (planted and sliding back at a steady rate under her, then lifted
    // and swung forward), and the hip, knee and ankle angles are solved to put it there (two-bone IK).
    const double StanceEnd = 0.6, StrideFwd = 95, StrideBack = 95, FootLift = 34;
    double thighLen = 171, shinLen = 198;
    // Extra ankle angle on top of keeping the foot flat (+ = toes down): heel first at strike,
    // push off at toe-off, toes up while swinging. The last key repeats the first (looping).
    static readonly double[][] AnkleKeys = { new[] { 0, 0.12, 0.45, 0.6, 0.75, 0.9, 1.0 }, new[] { -8.0, 0, 0, 22, 6, -10, -8 } };

    // Where her foot is (forward of the hip, and how far below it) for gait phase u
    void FootAt(double u, out double f, out double d)
    {
        double reach = (thighLen + shinLen) * 0.997;
        if (u < StanceEnd)
        {
            // Planted: slides back steadily relative to the hip; the leg stays nearly straight, so the hip
            // rises and falls naturally over the foot
            f = StrideFwd - (StrideFwd + StrideBack) * (u / StanceEnd);
            d = Math.Sqrt(reach * reach - f * f);
            return;
        }
        double k = (u - StanceEnd) / (1 - StanceEnd);
        double e = k * k * (3 - 2 * k);
        f = -StrideBack + (StrideFwd + StrideBack) * e;
        // Lift it clear of the ground under the other (planted) foot
        double uOther = (u + 0.5) % 1;
        double fo = StrideFwd - (StrideFwd + StrideBack) * (Math.Min(uOther, StanceEnd) / StanceEnd);
        d = Math.Sqrt(reach * reach - fo * fo) - FootLift * Math.Sin(Math.PI * k);
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
        double r = Math.Min(Math.Sqrt(f * f + d * d), thighLen + shinLen - 0.5);
        double cb = (r * r - thighLen * thighLen - shinLen * shinLen) / (2 * thighLen * shinLen);
        double beta = Math.Acos(Math.Max(-1, Math.Min(1, cb)));
        double cg = (thighLen * thighLen + r * r - shinLen * shinLen) / (2 * thighLen * r);
        double gamma = Math.Acos(Math.Max(-1, Math.Min(1, cg)));
        double alpha = Math.Atan2(f, d) + gamma; // the knee sits in front of the hip-to-ankle line
        hip = alpha * 180 / Math.PI;
        knee = beta * 180 / Math.PI;
        // Keeping the foot flat means matching the shin's tilt; the keys add heel strike and push-off
        ankle = (hip - knee) + Spline(AnkleKeys, u);
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
        kickPh += dt * 1.7; // lazy kicking while she reads
        swingPh += dt * 2.6 * (1 - 0.45 * lookAmt);
        if (state == "walk" || segTo == "walk") walkPh += dt * 2 * Math.PI * StepHz;

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
            if (n == "walk" || IsFloor(n))
                turning = Math.Abs(yawNow - (n == "walk" ? 90 * WalkDir : FloorYaw * NextSide)) > 15;
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
        }
        if (segTo == null) { endedMoving = false; anchor = null; }

        // Turn smoothly toward wherever she should be facing
        double wantYaw = YawTarget(t);
        if (yawSnap || dt <= 0) { yawMid = yawNow = wantYaw; yawSnap = false; }
        double ya = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 7);
        yawMid += (wantYaw - yawMid) * ya;
        yawNow += (yawMid - yawNow) * ya;

        BPose p;
        walkBlend = state == "walk" ? 1 : 0;
        segK = 0;
        if (segTo != null)
        {
            double k = Math.Max(0, Math.Min(1, (t - segStart) / segDur));
            segK = k;
            double e = segIn && segOut ? k : segOut ? k * k * (2 - k) : segIn ? k * (1 + k - k * k) : Ease(k);
            p = Lerp(from, PoseFor(segTo, t), e);
            Arc(p, state, segTo, Math.Sin(Math.PI * k));
            if (segTo == "walk") walkBlend = e;
            else if (state == "walk") walkBlend = 1 - e;
        }
        else p = PoseFor(state, t);
        shown = p;

        foreach (var kv in p.J) Set(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2]);
        double since = t - nodAt;
        if (since < 0.9)
        {
            var h = Model["bone009"];
            if (h != null) h.Ry -= 13 * Math.Abs(Math.Sin(since / 0.9 * Math.PI * 2));
        }
        Model.Update();

        // Keep her on the taskbar: her seat on the edge when sitting, otherwise whatever part of her is lowest
        pitchRot.Angle = p.Pitch;
        turn.Angle = yawNow;
        double low = Model.LowestZ(p.Pitch, hipY, hipZ);
        double seat = hipZ + (seatZ - hipZ) * Math.Cos(p.Pitch * Math.PI / 180);
        lift.OffsetZ = -(p.SeatW * seat + (1 - p.SeatW) * low) - p.Sink;

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
    double segK, bookAmt, pageStart = -100, nextPage = 14;
    const double PageW = 60, PageH = 88, CoverT = 2.5, PagesT = 5;

    void PlaceBook(double t)
    {
        if (bookVisual == null) return;
        double want = 0;
        if (state == "lie" && segTo == null) want = 1;
        else if (segTo == "lie") want = Ease(Math.Max(0, Math.Min(1, (segK - 0.25) / 0.75)));
        else if (state == "lie") want = 1 - Ease(Math.Min(1, segK / 0.6));
        bookAmt = want;
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
        bookPlace.Matrix = new Matrix3D(across.X, across.Y, across.Z, 0, ahead.X, ahead.Y, ahead.Z, 0, 0, 0, 1, 0, c.X, c.Y, c.Z, 1);
        bookScale.ScaleX = bookScale.ScaleY = bookScale.ScaleZ = bookAmt;

        // Page turns: the page lifts off the right side, arcs over the spine, and settles on the left
        if (state == "lie" && segTo == null && !Looking && t >= nextPage && t - pageStart > 2)
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
            half.Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), s < 0 ? 8 : -8));
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
    void Head(BPose p, double yaw, double pitch, double roll, double lYaw, double lPitch, double lRoll)
    {
        double L = lookAmt;
        J(p, "bone009", yaw + (lYaw - yaw) * L, pitch + (lPitch - pitch) * L, roll + (lRoll - roll) * L);
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
                p.Pitch = 90; p.Sink = 25;
                ReadingArms(p, br);
                J(p, "bone007", 0, 80 + 22 * Math.Sin(kickPh), 0);
                J(p, "bone008", 0, 80 - 22 * Math.Sin(kickPh + 0.9), 0);
                J(p, "bone021", 0, 22, 0); J(p, "bone027", 0, 22, 0);
                ReadingHead(p, t, L);
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
            case "kneel":
            {
                p.Pitch = 8; p.Sink = 4;
                J(p, "bone003", 0, -8, 0); J(p, "bone005", 0, -8, 0);
                J(p, "bone007", 0, 84, 0); J(p, "bone008", 0, 84, 0);
                J(p, "bone021", 0, 70, 0); J(p, "bone027", 0, 70, 0);
                J(p, "bone026", 0, -20, 36 + br); J(p, "bone032", 0, -20, -36 - br);
                J(p, "bone034", 25, 0, 0); J(p, "bone036", -25, 0, 0);
                Head(p, (-25 + 6 * Math.Sin(t * 0.35)) * side, 2 * Math.Sin(t * 0.8), 2 * Math.Sin(t * 0.27) * side, -62 * side, 10, -6 * side);
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
                J(p, "bone026", -10, -40, 34); J(p, "bone032", -10, -40, -34);
                J(p, "bone034", 40, 0, 0); J(p, "bone036", -40, 0, 0);
                Head(p, 4 * Math.Sin(t * 0.5), -6 + 2 * Math.Sin(t * 0.8), 0, 0, 14, 0);
                break;
            }
            case "sit":
            {
                // Sitting on the edge, legs swinging over the taskbar. Noticing you: she looks up at you.
                p.SeatW = 1; p.Sink = 0;
                double sw = Math.Sin(swingPh);
                J(p, "bone003", 0, -90, 0); J(p, "bone005", 0, -90, 0);
                J(p, "bone007", 0, 86 + 24 * sw, 0); J(p, "bone008", 0, 86 - 24 * sw, 0);
                J(p, "bone021", 0, 12, 0); J(p, "bone027", 0, 12, 0);
                foreach (var sk in Skirt) J(p, sk, 0, 75, 0);
                J(p, "bone026", -15, 0, 40 + br); J(p, "bone032", -15, 0, -40 - br);
                J(p, "bone034", 45, 0, 0); J(p, "bone036", -45, 0, 0);
                Head(p, 14 * Math.Sin(t * 0.31) + 6 * Math.Sin(t * 0.83), 2 * Math.Sin(t * 0.8), 2.5 * Math.Sin(t * 0.27), 0, 16, 5);
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
                double amp = 0.12 * (1 + 0.12 * Math.Sin(t * 0.53) + 0.06 * Math.Sin(t * 1.37));
                double armL = amp * FootF(uL - 0.05) - 5, armR = amp * FootF(uR - 0.05) - 5; // + = back
                double elbL = amp * FootF(uL - 0.14) - 5, elbR = amp * FootF(uR - 0.14) - 5;
                J(p, "bone026", 0, armL, 27 + 0.6 * br); J(p, "bone032", 0, armR, -27 - 0.6 * br);
                J(p, "bone034", 20 + 0.6 * Math.Max(0, -elbL), 0, 0); J(p, "bone036", -(20 + 0.6 * Math.Max(0, -elbR)), 0, 0);
                // Shoulders twist against the hips; the spine leans in a touch and dips with each step
                double twist = 0.028 * (FootF(uL) - FootF(uR));
                J(p, "bone002", twist, -3 + 1.2 * Math.Sin(4 * Math.PI * u - 0.5), 0);
                // Head: steady, but not frozen. A soft bob a beat after each step, a slight sway, and
                // slow glances around.
                Head(p, -0.6 * twist + 6 * Math.Sin(t * 0.37) + 3 * Math.Sin(t * 0.91),
                    3 + 1.2 * Math.Sin(4 * Math.PI * u - 1.1) + 2 * Math.Sin(t * 0.29),
                    1.5 * Math.Sin(2 * Math.PI * u), -60 * WalkDir, 8, 0);
                break;
            }
            case "flat":
            {
                // Lying flat on her stomach with her legs stretched out: the step between crawling and
                // kicking her feet up, so lying down looks like a real person getting comfortable.
                p.Pitch = 90; p.Sink = 25;
                ReadingArms(p, br);
                J(p, "bone007", 0, 10, 0); J(p, "bone008", 0, 14, 0);
                J(p, "bone021", 0, 45, 0); J(p, "bone027", 0, 45, 0);
                ReadingHead(p, t, L);
                break;
            }
            default: // stand
            {
                J(p, "bone026", 0, 0, 38 + br); J(p, "bone032", 0, 0, -38 - br);
                J(p, "bone034", 12, 0, 0); J(p, "bone036", -12, 0, 0);
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
    void ReadingArms(BPose p, double br)
    {
        J(p, "bone002", 0, 18 + 1.2 * br, 0);
        J(p, "bone026", 86, -68, 41); J(p, "bone034", 70, 0, 0); J(p, "bone039", -9, 19, 0);
        J(p, "bone032", -91, -62, -63); J(p, "bone036", -63, 0, 0); J(p, "bone041", -17, -2, 0);
    }

    // Eyes on the book, slowly following the lines and dipping a little at the end of each one;
    // noticing you, she lifts her head and turns to look at you (with a small twist of the shoulders).
    void ReadingHead(BPose p, double t, double L)
    {
        double scan = 4 * Math.Sin(t * 0.8) + 1.5 * Math.Sin(t * 2.3);
        double dip = 1.5 * Math.Sin(t * 0.5);
        double[] neckRead = { 2 + scan * 0.4, 17 + dip, -2 }, headRead = { -5 + scan * 0.6, 6, 0 };
        double[] neckLook = side > 0 ? new double[] { -23, 21, 14 } : new double[] { 23, 26, -4 };
        double[] headLook = side > 0 ? new double[] { -26, 16, -5 } : new double[] { 28, 14, -16 };
        double twist = (side > 0 ? -25 : 12) * L;
        J(p, "bone004", twist, 10, 0);
        J(p, "bone006", neckRead[0] + (neckLook[0] - neckRead[0]) * L, neckRead[1] + (neckLook[1] - neckRead[1]) * L, neckRead[2] + (neckLook[2] - neckRead[2]) * L);
        J(p, "bone009", headRead[0] + (headLook[0] - headRead[0]) * L, headRead[1] + (headLook[1] - headRead[1]) * L, headRead[2] + (headLook[2] - headRead[2]) * L);
    }

    // Extra bend through the middle of some moves, so kneeling down dips through a half squat
    static void Arc(BPose p, string a, string b, double w)
    {
        if (w <= 0) return;
        if ((a == "stand" && b == "kneel") || (a == "kneel" && b == "stand"))
        {
            p.Pitch += 14 * w;
            Add(p, "bone003", 0, -40 * w, 0); Add(p, "bone005", 0, -40 * w, 0);
            Add(p, "bone007", 0, 35 * w, 0); Add(p, "bone008", 0, 35 * w, 0);
            foreach (var sk in Skirt) Add(p, sk, 0, 20 * w, 0);
        }
    }

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
// quiz, drag her to move her, right-click to hide her for a while.
class BuddyWindow : Window
{
    // GroundY: where the taskbar's top edge falls in the window
    const double W = 560, H = 570, GroundY = 460, WorldPerDip = 2.3;
    readonly HeatherView view;
    readonly Action onClick;
    readonly Action<double> saveX;
    readonly Border bubble;
    readonly TextBlock bubbleText;
    readonly DispatcherTimer watch;
    bool walking;
    readonly Random rng = new Random();
    Point downAt;
    double downLeft, walkTarget;
    string afterWalk = "sit";
    bool dragging, fullscreenApp, pausedWalk, wasSnoozed;
    DateTime nextChange, snoozeUntil = DateTime.MinValue;
    public bool Suppressed, Disabled;
    // Told when she goes into hiding (with how long, in words) so the app can say how to bring her back
    public Action<string> Snoozed;

    public bool IsSnoozed { get { return DateTime.Now < snoozeUntil; } }

    static readonly string[] Lines = { "Study time?", "Click me. I'm bored.", "Quiz?", "I'm waiting...", "Let's learn something.", "Oh. Hey." };

    public BuddyWindow(HeatherModel model, double x, Action<double> saveX, Action onClick)
    {
        this.onClick = onClick;
        this.saveX = saveX;
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
        Left = double.IsNaN(x) ? Clamp(wa.Right - W - 150) : Clamp(x);
        Top = wa.Bottom - GroundY;

        var root = new Grid { Background = Brushes.Transparent };
        view = new HeatherView(model, false, W, H, new Point3D(0, 5, 0), WorldPerDip, new Vector3D(0.1, 1, -0.3), GroundY, true);
        view.Fps = 24; // gentle idle motion looks fine at 24 and keeps CPU low
        view.NextSide = SideAt(Left, rng.Next(2) == 0 ? 1 : -1);
        view.SnapTo(rng.Next(2) == 0 ? "lie" : "sit");
        // After she gets up, the window takes over any sideways offset so she never slides
        view.ShiftWindow = d => { if (!dragging) Left = Clamp(Left + d); };
        view.Cursor = Cursors.Hand;
        view.ToolTip = "Click to study · drag to move · right-click to hide her";
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
        view.MouseEnter += (s, e) =>
        {
            view.Looking = true;
            if (view.Activity == "walk") { pausedWalk = true; view.Activity = "stand"; }
            ShowBubble(Lines[rng.Next(Lines.Length)], false);
        };
        view.MouseLeave += (s, e) =>
        {
            view.Looking = false;
            bubble.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            if (pausedWalk && !dragging)
            {
                pausedWalk = false;
                view.Activity = "walk";
            }
        };
        view.MouseLeftButtonDown += (s, e) =>
        {
            downAt = PointToScreen(e.GetPosition(this));
            downLeft = Left;
            dragging = false;
            view.CaptureMouse();
        };
        view.MouseMove += (s, e) =>
        {
            if (!view.IsMouseCaptured) return;
            var p = PointToScreen(e.GetPosition(this));
            var src = PresentationSource.FromVisual(this);
            double scale = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            double dx = (p.X - downAt.X) / scale;
            if (Math.Abs(dx) > 4 && !dragging)
            {
                dragging = true;
                StopWalking();
            }
            if (dragging) Left = Clamp(downLeft + dx);
        };
        view.MouseLeftButtonUp += (s, e) =>
        {
            view.ReleaseMouseCapture();
            if (dragging)
            {
                saveX(Left);
                // Settle back down soon after being moved
                nextChange = DateTime.Now.AddSeconds(6 + rng.Next(6));
            }
            else onClick();
            dragging = false;
        };

        SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };

        // Walking moves the window in step with each animation frame (smooth, and in time with her feet)
        view.Stepped = WalkStep;

        // Keep a showing bubble over her head
        CompositionTarget.Rendering += (s, e) => { if (IsVisible && bubble.Opacity > 0.01) PlaceBubble(); };

        // Stay above the taskbar, step out of the way of fullscreen apps, and now and then do something new
        nextChange = DateTime.Now.AddSeconds(20 + rng.Next(25));
        watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watch.Tick += (s, e) =>
        {
            fullscreenApp = Native.ForegroundIsFullscreen();
            bool snoozed = IsSnoozed;
            Refresh();
            if (wasSnoozed && !snoozed && IsVisible) Say("I'm back. Miss me?");
            wasSnoozed = snoozed;
            if (!IsVisible) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Top = SystemParameters.WorkArea.Bottom - GroundY;
            string settled = view.Settled;
            if (DateTime.Now >= nextChange && !dragging && !view.Looking && !pausedWalk && settled != null && settled != "walk")
                DoSomething();
        };
        watch.Start();
    }

    // Keep her middle at least 80 DIPs inside the screen
    static double Clamp(double x)
    {
        var wa = SystemParameters.WorkArea;
        return Math.Max(wa.Left - W / 2 + 80, Math.Min(wa.Right - W / 2 - 80, x));
    }

    // Which way she should lie so her head stays on screen: toward the middle when she's near an edge
    static int SideAt(double left, int preferred)
    {
        var wa = SystemParameters.WorkArea;
        double cx = left + W / 2;
        if (cx > wa.Right - 240) return -1;
        if (cx < wa.Left + 240) return 1;
        return preferred;
    }

    void DoSomething()
    {
        nextChange = DateTime.Now.AddSeconds(30 + rng.Next(40));
        string now = view.Settled ?? view.Activity;
        double r = rng.NextDouble();
        string next = r < 0.42 ? "lie" : r < 0.84 ? "sit" : "stand";

        // Most of the time she gets up and walks somewhere else first, then settles in there
        double target;
        if (rng.NextDouble() < 0.7 && PickSpot(out target))
        {
            walkTarget = target;
            afterWalk = next;
            view.WalkDir = target > Left ? 1 : -1;
            view.NextSide = SideAt(target, view.WalkDir);
            view.Activity = "walk";
            walking = true;
            return;
        }
        if (next == now) next = now == "lie" ? "sit" : "lie";
        if (next == "lie") view.NextSide = SideAt(Left, rng.Next(2) == 0 ? 1 : -1);
        view.Activity = next;
    }

    // A random spot on the taskbar that's a real walk away
    bool PickSpot(out double target)
    {
        var wa = SystemParameters.WorkArea;
        target = Left;
        for (int i = 0; i < 12; i++)
        {
            double t = Clamp(wa.Left - W / 2 + 80 + rng.NextDouble() * (wa.Width - 160));
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
        if (step > 0)
        {
            int dir = view.WalkDir;
            double next = Left + dir * step;
            if ((walkTarget - next) * dir <= 0) next = walkTarget;
            Left = Clamp(next);
        }
        if (view.Activity != "walk" && view.WalkBlend <= 0 && !pausedWalk)
        {
            walking = false;
            saveX(Left);
        }
    }

    void StopWalking()
    {
        walking = false;
        pausedWalk = false;
        if (view.Activity == "walk") view.Activity = "stand";
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
        if (IsVisible) Say("I'm back. Miss me?");
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

    public void Say(string text) { ShowBubble(text, true); }

    // Just above her head (called while the bubble shows, so it follows her if she moves)
    void PlaceBubble()
    {
        bubble.Margin = new Thickness(0); // measuring includes the margin, so clear the old position first
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = bubble.DesiredSize.Width, bh = bubble.DesiredSize.Height;
        var head = view.HeadPoint();
        if (double.IsNaN(head.X) || double.IsNaN(head.Y)) head = new Point(W / 2, GroundY - 300);
        bubble.Margin = new Thickness(Math.Max(4, Math.Min(W - bw - 4, head.X - bw / 2)), Math.Max(2, head.Y - 78 - bh), 0, 0);
    }
}

static class Native
{
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

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
