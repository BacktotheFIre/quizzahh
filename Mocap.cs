using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Media3D;

// Real motion for taskbar Heather. CMU's motion-capture library (mocap.cs.cmu.edu: "may be copied,
// modified, or redistributed without permission") records real people as a skeleton file (.asf) and
// a motion file (.amc). "--bake" copies that motion onto her skeleton (retargeting) and writes her
// own joint angles to a clips file, which build.ps1 builds into the app like her model.
static class Mocap
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    // Her joints that recorded motion drives, each with the CMU bone it copies and the joint its bone
    // points at (to line the two skeletons up). In parent-before-child order.
    static readonly string[][] Map =
    {
        new[] { "bone002", "upperback", "bone004" }, new[] { "bone004", "thorax", "bone006" },
        new[] { "bone006", "lowerneck", "bone009" }, new[] { "bone009", "head", "headtop" },
        // (not the hands: CMU's hand markers are noisy, and they jerked her wrists about; her hands follow her forearms)
        new[] { "bone026", "lhumerus", "bone034" }, new[] { "bone034", "lradius", "bone039" },
        new[] { "bone032", "rhumerus", "bone036" }, new[] { "bone036", "rradius", "bone041" },
        new[] { "bone003", "lfemur", "bone007" }, new[] { "bone007", "ltibia", "bone021" }, new[] { "bone021", "lfoot", "bone033" },
        new[] { "bone005", "rfemur", "bone008" }, new[] { "bone008", "rtibia", "bone027" }, new[] { "bone027", "rfoot", "bone035" },
    };
    public static string[] Joints { get { return Map.Select(m => m[0]).ToArray(); } }

    // CMU's axes are x = the actor's left, y = up, z = forward; hers are x = left, z = up, -y = forward
    static readonly double[] P = { 1, 0, 0, 0, 0, -1, 0, 1, 0 };

    class Bone { public string Name, Parent; public double[] Dir = { 0, 0, 0 }, C = Id(); public string[] Dof = new string[0]; }

    // CS-Quiz-test.exe --bake <folder with .asf/.amc files> <clips file to write>
    // Every .amc becomes one clip named after it (like "140_01"), 30 frames a second: a line per frame
    // with her body's forward tilt, then three angles for each joint in Joints.
    public static void Bake(string dir, string outFile)
    {
        var log = new StringBuilder();
        var model = QuizWindow.LoadModel();
        model.ResetPose(); model.UpdateJoints();
        // Her rest pose: each joint's world rotation, and which way its bone points
        var rest = new Dictionary<string, double[]>(); var hdir = new Dictionary<string, double[]>();
        foreach (var m in Map)
        {
            rest[m[0]] = M3(model[m[0]].World);
            var a = model.JointPos(m[0]);
            var b = m[2] == "headtop" ? Mid(model.JointPos("bone022"), model.JointPos("bone028")) : Mid(model.JointPos(m[2]), model.JointPos(m[2]));
            hdir[m[0]] = Norm(new[] { b[0] - a.X, b[1] - a.Y, b[2] - a.Z });
        }
        var outp = new StringBuilder();
        outp.AppendLine("joints " + string.Join(" ", Joints));
        foreach (var amc in Directory.GetFiles(dir, "*.amc").OrderBy(f => f))
        {
            string id = Path.GetFileNameWithoutExtension(amc), subj = id.Split('_')[0];
            var bones = ReadAsf(Path.Combine(dir, subj + ".asf"));
            var frames = ReadAmc(amc);
            // Line the rest poses up: CMU stands in a T (arms straight out), she stands with her arms down
            var align = new Dictionary<string, double[]>();
            foreach (var m in Map) align[m[0]] = FromTo(hdir[m[0]], Norm(Apply(P, bones[m[1]].Dir)));
            var lines = new List<string>();
            var prev = new Dictionary<string, double[]>();
            for (int f = 0; f < frames.Count; f += 4) // 120 frames a second -> 30
            {
                var D = Deltas(bones, frames[f]);
                foreach (var k in D.Keys.ToList()) D[k] = Mul(Mul(P, D[k]), T(P));
                // Which way she faces is the app's job: turn the whole recording so her hips face forward
                var left = Apply(D["root"], new double[] { 1, 0, 0 });
                var face = Rz(-Math.Atan2(left[1], left[0]) * 180 / Math.PI);
                foreach (var k in D.Keys.ToList()) D[k] = Mul(face, D[k]);
                // Her body's forward tilt is the app's too (it tips her round her pelvis): take it out
                var up = Apply(D["root"], new double[] { 0, 0, 1 });
                double pitch = Math.Atan2(-up[1], up[2]) * 180 / Math.PI;
                var untilt = T(Rx(pitch));
                model.ResetPose();
                var sb = new StringBuilder(pitch.ToString("0.0", CI));
                foreach (var m in Map)
                {
                    model.UpdateJoints();
                    var j = model[m[0]];
                    var want = Mul(untilt, Mul(D[m[1]], Mul(align[m[0]], rest[m[0]])));
                    var frame0 = Mul(M3(model.Joints[j.Parent].World), M3(j.Local)); // the joint's axes with no pose of its own
                    double[] e, pe;
                    prev.TryGetValue(m[0], out pe);
                    int hinge = Hinge(m[0]);
                    if (hinge >= 0)
                    {
                        // Knees only bend: just the angle round their hinge that points the bone
                        // the right way (a full 3-angle twist there flips about and isn't real anyway)
                        var r = Apply(T(rest[m[0]]), hdir[m[0]]); // the bone in its own axes
                        var d = Apply(T(frame0), Apply(want, Apply(T(rest[m[0]]), hdir[m[0]])));
                        r[hinge] = 0; d[hinge] = 0;
                        int a1 = (hinge + 1) % 3, a2 = (hinge + 2) % 3;
                        double ang = Math.Atan2(r[a1] * d[a2] - r[a2] * d[a1], r[a1] * d[a1] + r[a2] * d[a2]) * 180 / Math.PI;
                        // (and only the way a real knee bends: nearly straight, the direction flipped about)
                        ang = Math.Max(0, ang);
                        e = new double[3]; e[hinge] = ang;
                    }
                    else
                    {
                        e = EulerXYZ(Mul(T(frame0), want));
                        // Every rotation has two sets of X, Y, Z angles: take the one nearer the last frame,
                        // so the angles never flip between them
                        if (pe != null)
                        {
                            var alt = new[] { e[0] + 180, 180 - e[1], e[2] + 180 };
                            if (Dist(alt, pe) < Dist(e, pe)) e = alt;
                        }
                    }
                    // keep each angle close to the frame before, so it never jumps by 360
                    if (pe != null) for (int q = 0; q < 3; q++) { while (e[q] - pe[q] > 180) e[q] -= 360; while (e[q] - pe[q] < -180) e[q] += 360; }
                    prev[m[0]] = e;
                    j.Rx = e[0]; j.Ry = e[1]; j.Rz = e[2];
                    foreach (var v in e) sb.Append(' ').Append(v.ToString("0.0", CI));
                }
                lines.Add(sb.ToString());
            }
            // Smooth out the markers' jitter: each angle averaged with the three frames either side
            var rows = lines.Select(l => l.Split(' ').Select(v => double.Parse(v, CI)).ToArray()).ToList();
            outp.AppendLine("clip " + id + " 30 " + rows.Count);
            for (int f = 0; f < rows.Count; f++)
            {
                int a0 = Math.Max(0, f - 3), a1 = Math.Min(rows.Count - 1, f + 3);
                outp.AppendLine(string.Join(" ", Enumerable.Range(0, rows[f].Length).Select(n => Enumerable.Range(a0, a1 - a0 + 1).Average(g => rows[g][n]).ToString("0.0", CI))));
            }
            log.AppendLine(id + ": " + lines.Count + " frames");
        }
        log.AppendLine(Check() ?? "math check passed");
        File.WriteAllText(outFile, outp.ToString());
        File.WriteAllText(outFile + ".log", log.ToString());
    }

    // The baked clips built into the app (empty without model-src\clips.txt): name -> frames, each
    // frame her body's forward tilt then three angles per joint in Joints. 30 frames a second.
    static Dictionary<string, List<double[]>> clips;
    public static Dictionary<string, List<double[]>> Clips
    {
        get
        {
            if (clips != null) return clips;
            clips = new Dictionary<string, List<double[]>>();
            var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("clips.txt");
            if (s == null) return clips;
            using (var r = new StreamReader(s))
            {
                string line; List<double[]> cur = null;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.StartsWith("joints")) continue;
                    if (line.StartsWith("clip ")) { cur = new List<double[]>(); clips[line.Split(' ')[1]] = cur; continue; }
                    if (cur != null && line.Length > 0) cur.Add(line.Split(' ').Select(v => double.Parse(v, CI)).ToArray());
                }
            }
            return clips;
        }
    }

    // One frame of a clip, t seconds into the part from a to b seconds (a > b plays it backwards).
    // Looping, the end fades into the start over 0.6 s so there's no jump. Null if the clip isn't built in.
    public static double[] Sample(string src, double a, double b, double t, bool loop)
    {
        List<double[]> c;
        if (!Clips.TryGetValue(src, out c) || c.Count < 2) return null;
        double len = Math.Abs(b - a), dir = Math.Sign(b - a);
        if (!loop) return At(c, a + dir * Math.Max(0, Math.Min(len, t)));
        const double X = 0.6;
        double period = len - X, u = ((t % period) + period) % period;
        var f = At(c, a + dir * u);
        if (u >= X) return f;
        var g = At(c, a + dir * (period + u)); // (the end of the loop, fading out as the start fades in)
        double w = u / X;
        return f.Select((v, i) => g[i] + (v - g[i]) * w).ToArray();
    }

    // A frame between two recorded ones (30 a second), in a straight line between them
    static double[] At(List<double[]> c, double sec)
    {
        double x = Math.Max(0, Math.Min(c.Count - 1, sec * 30));
        int i = Math.Min(c.Count - 2, (int)x); double k = x - i;
        return c[i].Select((v, n) => v + (c[i + 1][n] - v) * k).ToArray();
    }

    // Knees bend round their Y axis (as in her hand-made poses); -1 = not a hinge
    // (Elbows aren't treated as hinges: lining her arms up with the actor's twists them slightly, so the
    // recorded bend lands off her elbow's own axis, and only a full three-angle turn reproduces it)
    static int Hinge(string j) { return j == "bone007" || j == "bone008" ? 1 : -1; }

    // How far apart two sets of angles are (each angle compared the short way round)
    static double Dist(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < 3; i++) { double d = ((a[i] - b[i]) % 360 + 540) % 360 - 180; s += d * d; }
        return s;
    }

    // Each CMU bone's rotation away from its rest pose, in CMU's world axes. A bone's motion turns it
    // in its own axes (C), on top of everything above it.
    static Dictionary<string, double[]> Deltas(Dictionary<string, Bone> bones, Dictionary<string, double[]> frame)
    {
        var D = new Dictionary<string, double[]>();
        double[] rv;
        var root = frame.TryGetValue("root", out rv) ? EulerAsf(rv[3], rv[4], rv[5]) : Id();
        D["root"] = root;
        Action<string> walk = null;
        walk = name =>
        {
            foreach (var b in bones.Values.Where(x => x.Parent == name))
            {
                var r = new double[3];
                double[] v;
                if (frame.TryGetValue(b.Name, out v)) for (int i = 0; i < b.Dof.Length && i < v.Length; i++) r["xyz".IndexOf(b.Dof[i][1])] = v[i];
                D[b.Name] = Mul(D[name], Mul(b.C, Mul(EulerAsf(r[0], r[1], r[2]), T(b.C))));
                walk(b.Name);
            }
        };
        walk("root");
        return D;
    }

    static Dictionary<string, Bone> ReadAsf(string path)
    {
        var bones = new Dictionary<string, Bone>();
        Bone cur = null; string section = "";
        foreach (var raw in File.ReadAllLines(path))
        {
            var t = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0 || t[0].StartsWith("#")) continue;
            if (t[0].StartsWith(":")) { section = t[0]; continue; }
            if (section == ":bonedata")
            {
                if (t[0] == "begin") cur = new Bone();
                else if (t[0] == "end") { bones[cur.Name] = cur; cur = null; }
                else if (cur == null) continue;
                else if (t[0] == "name") cur.Name = t[1];
                else if (t[0] == "direction") cur.Dir = t.Skip(1).Take(3).Select(s => double.Parse(s, CI)).ToArray();
                else if (t[0] == "axis") cur.C = EulerAsf(double.Parse(t[1], CI), double.Parse(t[2], CI), double.Parse(t[3], CI));
                else if (t[0] == "dof") cur.Dof = t.Skip(1).ToArray();
            }
            else if (section == ":hierarchy" && t[0] != "begin" && t[0] != "end")
                foreach (var c in t.Skip(1)) if (bones.ContainsKey(c)) bones[c].Parent = t[0];
        }
        return bones;
    }

    static List<Dictionary<string, double[]>> ReadAmc(string path)
    {
        var frames = new List<Dictionary<string, double[]>>();
        Dictionary<string, double[]> cur = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var t = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0 || t[0].StartsWith("#") || t[0].StartsWith(":")) continue;
            int n;
            if (t.Length == 1 && int.TryParse(t[0], out n)) { cur = new Dictionary<string, double[]>(); frames.Add(cur); continue; }
            if (cur != null) cur[t[0]] = t.Skip(1).Select(s => double.Parse(s, CI)).ToArray();
        }
        return frames;
    }

    // ---------- 3x3 rotation math (row-major, turning column vectors) ----------
    static double[] Id() { return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }; }
    static double[] Mul(double[] a, double[] b)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }
    static double[] T(double[] a) { return new[] { a[0], a[3], a[6], a[1], a[4], a[7], a[2], a[5], a[8] }; }
    static double[] Apply(double[] m, double[] v) { return new[] { m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2] }; }
    static double[] Rx(double d) { double a = d * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[] { 1, 0, 0, 0, c, -s, 0, s, c }; }
    static double[] Ry(double d) { double a = d * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[] { c, 0, s, 0, 1, 0, -s, 0, c }; }
    static double[] Rz(double d) { double a = d * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[] { c, -s, 0, s, c, 0, 0, 0, 1 }; }
    // CMU's angles turn about x, then y, then z
    static double[] EulerAsf(double x, double y, double z) { return Mul(Rz(z), Mul(Ry(y), Rx(x))); }
    // Her joints combine X * Y * Z (see HeatherModel.Euler): split a rotation back into those three
    static double[] EulerXYZ(double[] m)
    {
        double b = Math.Asin(Math.Max(-1, Math.Min(1, m[2])));
        double a = Math.Atan2(-m[5], m[8]), c = Math.Atan2(-m[1], m[0]);
        return new[] { a * 180 / Math.PI, b * 180 / Math.PI, c * 180 / Math.PI };
    }
    static double[] Norm(double[] v) { double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); return new[] { v[0] / l, v[1] / l, v[2] / l }; }
    static double[] Mid(System.Windows.Media.Media3D.Point3D a, System.Windows.Media.Media3D.Point3D b) { return new[] { (a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2 }; }
    // The 3x3 rotation part of one of her 4x4 joint matrices, with any scale taken out
    static double[] M3(double[] m)
    {
        var r = new double[9];
        for (int c = 0; c < 3; c++)
        {
            double l = Math.Sqrt(m[c] * m[c] + m[4 + c] * m[4 + c] + m[8 + c] * m[8 + c]);
            for (int i = 0; i < 3; i++) r[i * 3 + c] = m[i * 4 + c] / l;
        }
        return r;
    }
    // The smallest rotation that turns direction u onto direction v
    static double[] FromTo(double[] u, double[] v)
    {
        var x = new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
        double c = u[0] * v[0] + u[1] * v[1] + u[2] * v[2], s = Math.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]);
        if (s < 1e-9) return Id();
        double[] k = { x[0] / s, x[1] / s, x[2] / s }; double t = 1 - c;
        return new[]
        {
            t * k[0] * k[0] + c, t * k[0] * k[1] - s * k[2], t * k[0] * k[2] + s * k[1],
            t * k[0] * k[1] + s * k[2], t * k[1] * k[1] + c, t * k[1] * k[2] - s * k[0],
            t * k[0] * k[2] - s * k[1], t * k[1] * k[2] + s * k[0], t * k[2] * k[2] + c
        };
    }

    // ---------- Keeping her hands out of her legs ----------
    // A real person's hands never go inside their own legs. Each frame, whatever posed her (a recording
    // or a hand-made pose), any point of an arm inside a thigh or shin swings that arm out from the
    // shoulder just far enough to clear it. Legs are tubes as thick as her own (measured from her mesh).
    static readonly string[][] LegTubes = { new[] { "bone003", "bone007" }, new[] { "bone007", "bone021" }, new[] { "bone005", "bone008" }, new[] { "bone008", "bone027" } };
    static readonly string[][] Arms =
    {
        // shoulder, then points along the arm: elbow, wrist, the middle of the hand, each fingertip and the thumb tip
        new[] { "bone026", "bone034", "bone039", "bone045", "bone061", "bone062", "bone063", "bone064", "bone071" },
        new[] { "bone032", "bone036", "bone041", "bone050", "bone066", "bone067", "bone068", "bone069", "bone072" },
    };
    static readonly double[] PointR = { 15, 12, 12, 6, 6, 6, 6, 6 }; // how thick the arm is at each of those points
    static double[] tubeR, bodyW, bodyD; // leg tube thicknesses; her body's half-width and half-depth in 8 slices, hips to neck

    // fix: each arm's correction last frame (shoulder angles added), kept by the caller; dt: seconds since
    // then (0 = a jump to a new pose: correct fully at once)
    // (upright: false when she's tipped over, lying or on all fours, where her hands rest by her chest on
    // the floor and "out of her body" would be down through the floor, propping her up on straight arms)
    public static void KeepArmsOut(HeatherModel m, Dictionary<string, double[]> pose, double[][] fix, double dt, bool upright)
    {
        if (tubeR == null)
        {
            // each leg tube's thickness: how far most of its own skin is from its middle line (80th percentile)
            var pts = m.SomePointsByJoint().ToList();
            tubeR = LegTubes.Select(t =>
            {
                var a = m.JointPos(t[0]); var b = m.JointPos(t[1]);
                var ds = pts.Where(kv => kv.Key != "" && m.Under(kv.Key, t[0]) && !m.Under(kv.Key, t[1]) && !HeatherView.Skirt.Contains(kv.Key))
                            .Select(kv => SegDist(V(kv.Value), V(a), V(b))).OrderBy(d => d).ToList();
                return ds.Count < 5 ? 40 : ds[(int)(ds.Count * 0.8)];
            }).ToArray();
            // her body, as the clipping test sees it: 8 ovals from her hips to her neck, as wide and deep as she is there
            Vector3D lo, up, across, deep; double len;
            BodyAxes(m, out lo, out up, out across, out deep, out len);
            var hw = Enumerable.Range(0, 8).Select(i => new List<double>()).ToArray();
            var hd = Enumerable.Range(0, 8).Select(i => new List<double>()).ToArray();
            foreach (var kv in pts)
            {
                if (kv.Key == "" || HeatherView.Skirt.Contains(kv.Key) || m.Under(kv.Key, "bone026") || m.Under(kv.Key, "bone032") || m.Under(kv.Key, "bone003") || m.Under(kv.Key, "bone005") || m.Under(kv.Key, "bone009")) continue;
                var dv = new Vector3D(kv.Value.X - lo.X, kv.Value.Y - lo.Y, kv.Value.Z - lo.Z);
                double t = Vector3D.DotProduct(dv, up) / len;
                if (t < 0 || t >= 1) continue;
                hw[(int)(t * 8)].Add(Math.Abs(Vector3D.DotProduct(dv, across))); hd[(int)(t * 8)].Add(Math.Abs(Vector3D.DotProduct(dv, deep)));
            }
            Func<List<double>, double> p90 = l => { if (l.Count < 5) return 0; l.Sort(); return l[(int)(l.Count * 0.9)]; };
            bodyW = hw.Select(p90).ToArray(); bodyD = hd.Select(p90).ToArray();
        }
        for (int ai = 0; ai < Arms.Length; ai++)
        {
            var arm = Arms[ai];
            var sh = m[arm[0]];
            if (sh == null) continue;
            var before = new[] { sh.Rx, sh.Ry, sh.Rz };
            for (int iter = 0; iter < 4; iter++)
            {
                m.UpdateJoints();
                double worst = 0; double[] at = null, outDir = null;
                var pts = arm.Skip(1).Select(n => V(m.JointPos(n))).ToList();
                pts.Insert(1, Mid3(pts[0], pts[1])); // (mid-forearm)
                pts.Insert(0, Mid3(V(m.JointPos(arm[0])), pts[0])); // (mid-upper-arm)
                var rs = new List<double>(PointR); rs.Insert(1, 14); rs.Insert(0, 16);
                for (int i = 0; i < pts.Count; i++)
                    for (int t = 0; t < LegTubes.Length; t++)
                    {
                        var a = V(m.JointPos(LegTubes[t][0])); var b = V(m.JointPos(LegTubes[t][1]));
                        var c = Closest(pts[i], a, b);
                        var d = Sub(pts[i], c); double dist = Len(d);
                        double pen = 0.9 * tubeR[t] + rs[i] - dist; // (all of that point's thickness outside the leg)
                        if (pen > worst && dist > 1e-6) { worst = pen; at = pts[i]; outDir = Scale(d, 1 / dist); }
                    }
                // ...and her other arm (upper arm and forearm), so her arms never pass through each other
                // (not lying down: asleep her arms are folded one over the other on purpose)
                if (upright)
                {
                    var other = Arms[1 - ai];
                    var os = V(m.JointPos(other[0])); var oe = V(m.JointPos(other[1])); var ow = V(m.JointPos(other[2]));
                    foreach (var seg in new[] { new[] { os, oe }, new[] { oe, ow } })
                        for (int i = 1; i < pts.Count; i++) // (not the upper arm's middle: near the shoulders the arms can't meet)
                        {
                            var c = Closest(pts[i], seg[0], seg[1]);
                            var d = Sub(pts[i], c); double dist = Len(d);
                            double pen = 16 + rs[i] - dist; // (an arm is about 16 thick)
                            if (pen > worst && dist > 1e-6) { worst = pen; at = pts[i]; outDir = Scale(d, 1 / dist); }
                        }
                }
                // ...and her body (not where the arm joins it, at the top)
                if (upright)
                {
                    Vector3D lo, up, across, deep; double len;
                    BodyAxes(m, out lo, out up, out across, out deep, out len);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var dv = new Vector3D(pts[i][0] - lo.X, pts[i][1] - lo.Y, pts[i][2] - lo.Z);
                        double t = Vector3D.DotProduct(dv, up) / len;
                        if (t < 0.05 || t > 0.85) continue;
                        int bin = (int)(t * 8);
                        double bw = bodyW[bin], dp = bodyD[bin];
                        if (bw <= 0 || dp <= 0) continue;
                        double a = Vector3D.DotProduct(dv, across), d = Vector3D.DotProduct(dv, deep);
                        double inside = Math.Sqrt(a * a / (bw * bw) + d * d / (dp * dp)); // 1 = on her skin
                        double pen = (0.85 - inside) * Math.Min(bw, dp) + rs[i];
                        if (pen > worst && inside > 1e-6)
                        {
                            var n = across * (a / (bw * bw)) + deep * (d / (dp * dp)); n.Normalize();
                            worst = pen; at = pts[i]; outDir = new[] { n.X, n.Y, n.Z };
                        }
                    }
                }
                if (worst < 0.5) break;
                // turn the arm about the shoulder so that point moves straight out of the leg
                var s0 = V(m.JointPos(arm[0]));
                var lever = Sub(at, s0);
                var axis = Cross(lever, outDir); double al = Len(axis);
                if (al < 1e-6) break;
                double ang = Math.Min(0.35, worst * 1.1 / Len(lever)); // radians, at most 20 degrees a step
                var turnW = Rodrigues(Scale(axis, 1 / al), ang);
                var w = Mul(turnW, M3(sh.World));
                var frame0 = Mul(M3(m.Joints[sh.Parent].World), M3(sh.Local));
                var e = EulerXYZ(Mul(T(frame0), w));
                var cur = new[] { sh.Rx, sh.Ry, sh.Rz };
                var alt = new[] { e[0] + 180, 180 - e[1], e[2] + 180 };
                if (Dist(alt, cur) < Dist(e, cur)) e = alt;
                for (int q = 0; q < 3; q++) { while (e[q] - cur[q] > 180) e[q] -= 360; while (e[q] - cur[q] < -180) e[q] += 360; }
                sh.Rx = e[0]; sh.Ry = e[1]; sh.Rz = e[2];
            }
            // The correction eases in and out (at most 700 degrees a second, just under what the smoothness test allows), so it never jerks the arm:
            // for a moment it may not be quite enough, but it never snaps
            var want = new[] { sh.Rx - before[0], sh.Ry - before[1], sh.Rz - before[2] };
            double step = dt <= 0 ? 1e9 : 700 * dt;
            for (int q = 0; q < 3; q++) fix[ai][q] += Math.Max(-step, Math.Min(step, want[q] - fix[ai][q]));
            sh.Rx = before[0] + fix[ai][0]; sh.Ry = before[1] + fix[ai][1]; sh.Rz = before[2] + fix[ai][2];
            pose[arm[0]] = new[] { sh.Rx, sh.Ry, sh.Rz };
        }
    }

    // Her body's own axes right now: from between her hips, up to her neck, across her shoulders, and front to back
    static void BodyAxes(HeatherModel m, out Vector3D lo3, out Vector3D up, out Vector3D across, out Vector3D deep, out double len)
    {
        var l = m.JointPos("bone003"); var r = m.JointPos("bone005");
        lo3 = new Vector3D((l.X + r.X) / 2, (l.Y + r.Y) / 2, (l.Z + r.Z) / 2);
        var nk = m.JointPos("bone006");
        up = new Vector3D(nk.X - lo3.X, nk.Y - lo3.Y, nk.Z - lo3.Z); len = up.Length; up /= len;
        across = m.JointPos("bone026") - m.JointPos("bone032");
        across -= up * Vector3D.DotProduct(across, up); across.Normalize();
        deep = Vector3D.CrossProduct(up, across);
    }

    static double[] V(System.Windows.Media.Media3D.Point3D p) { return new[] { p.X, p.Y, p.Z }; }
    static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    static double[] Scale(double[] a, double k) { return new[] { a[0] * k, a[1] * k, a[2] * k }; }
    static double[] Mid3(double[] a, double[] b) { return new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 }; }
    static double Len(double[] a) { return Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); }
    static double[] Cross(double[] u, double[] v) { return new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] }; }
    static double[] Closest(double[] p, double[] a, double[] b)
    {
        var ab = Sub(b, a); double l2 = ab[0] * ab[0] + ab[1] * ab[1] + ab[2] * ab[2];
        double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, (Sub(p, a)[0] * ab[0] + Sub(p, a)[1] * ab[1] + Sub(p, a)[2] * ab[2]) / l2));
        return new[] { a[0] + ab[0] * t, a[1] + ab[1] * t, a[2] + ab[2] * t };
    }
    static double SegDist(double[] p, double[] a, double[] b) { return Len(Sub(p, Closest(p, a, b))); }
    // A turn of ang radians about the unit axis k
    static double[] Rodrigues(double[] k, double ang)
    {
        double c = Math.Cos(ang), s = Math.Sin(ang), t = 1 - c;
        return new[]
        {
            t * k[0] * k[0] + c, t * k[0] * k[1] - s * k[2], t * k[0] * k[2] + s * k[1],
            t * k[0] * k[1] + s * k[2], t * k[1] * k[1] + c, t * k[1] * k[2] - s * k[0],
            t * k[0] * k[2] - s * k[1], t * k[1] * k[2] + s * k[0], t * k[2] * k[2] + c
        };
    }

    // Self-check for the math: a rotation split into X, Y, Z angles and put back together is the same
    public static string Check()
    {
        var m = Mul(Rx(30), Mul(Ry(-50), Rz(70)));
        var e = EulerXYZ(m);
        var back = Mul(Rx(e[0]), Mul(Ry(e[1]), Rz(e[2])));
        double err = m.Zip(back, (p, q) => Math.Abs(p - q)).Max();
        var ft = FromTo(Norm(new double[] { 1, 2, 3 }), Norm(new double[] { -2, 0, 1 }));
        var moved = Apply(ft, Norm(new double[] { 1, 2, 3 })); var want = Norm(new double[] { -2, 0, 1 });
        double err2 = moved.Zip(want, (p, q) => Math.Abs(p - q)).Max();
        return err < 1e-9 && err2 < 1e-9 ? null : "mocap math: Euler round trip off by " + err + ", FromTo off by " + err2;
    }
}
