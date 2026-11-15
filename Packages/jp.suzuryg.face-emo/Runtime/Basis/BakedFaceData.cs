using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Suzuryg.FaceEmo.Basis
{
    // Pure C# model + writer/reader of the FE1 format (see design_docs/BASIS_BAKED_FORMAT.md).
    public class BakedFaceData
    {
        public const string Header = "FE1";
        public class Target { public int Kind, Ref, Sub; public float[] Rest = new float[0]; }
        public class Expression { public string Name = ""; public List<int> Targets = new List<int>(); public List<float[]> Values = new List<float[]>(); }
        public class Branch { public int Base = -1, Left = -1, Right = -1, Both = -1, Flags; }
        public class Mode { public string Name = ""; public int Anim = -1, Flags; public int[] Table = NewTable(); }
        public class Group { public string Name = ""; public int Parent = -1; }
        public class Item { public int Group = -1; public char Type = 'M'; public int Index; }

        public List<Target> Targets = new List<Target>();
        public List<Expression> Expressions = new List<Expression>();
        public List<Branch> Branches = new List<Branch>();
        public List<Mode> Modes = new List<Mode>();
        public List<Group> Groups = new List<Group>();
        public List<Item> Items = new List<Item>();

        public static int[] NewTable() { var t = new int[64]; for (int i = 0; i < 64; i++) t[i] = -1; return t; }

        static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        static float P(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        static string Floats(float[] v) { var a = new string[v.Length]; for (int i = 0; i < v.Length; i++) a[i] = F(v[i]); return string.Join(",", a); }
        static float[] ParseFloats(string s) { if (s.Length == 0) return new float[0]; var p = s.Split(','); var r = new float[p.Length]; for (int i = 0; i < p.Length; i++) r[i] = P(p[i]); return r; }

        public static string Esc(string s) => s.Replace("%", "%25").Replace("|", "%7C").Replace(";", "%3B").Replace(":", "%3A").Replace(",", "%2C").Replace("\n", "%0A").Replace("\r", "");
        public static string Unesc(string s) => s.Replace("%0A", "\n").Replace("%2C", ",").Replace("%3A", ":").Replace("%3B", ";").Replace("%7C", "|").Replace("%25", "%");

        public string Write()
        {
            var sb = new StringBuilder(Header).Append('\n');
            foreach (var t in Targets) sb.Append("T|").Append(t.Kind).Append('|').Append(t.Ref).Append('|').Append(t.Sub).Append('|').Append(Floats(t.Rest)).Append('\n');
            foreach (var x in Expressions)
            {
                sb.Append("X|").Append(Esc(x.Name)).Append('|');
                for (int i = 0; i < x.Targets.Count; i++) { if (i > 0) sb.Append(';'); sb.Append(x.Targets[i]).Append(':').Append(Floats(x.Values[i])); }
                sb.Append('\n');
            }
            foreach (var b in Branches) sb.Append("B|").Append(b.Base).Append('|').Append(b.Left).Append('|').Append(b.Right).Append('|').Append(b.Both).Append('|').Append(b.Flags).Append('\n');
            foreach (var m in Modes) sb.Append("M|").Append(Esc(m.Name)).Append('|').Append(m.Anim).Append('|').Append(m.Flags).Append('|').Append(string.Join(",", m.Table)).Append('\n');
            foreach (var g in Groups) sb.Append("G|").Append(Esc(g.Name)).Append('|').Append(g.Parent).Append('\n');
            foreach (var i in Items) sb.Append("I|").Append(i.Group).Append('|').Append(i.Type).Append('|').Append(i.Index).Append('\n');
            return sb.ToString();
        }

        public static BakedFaceData Read(string text)
        {
            var d = new BakedFaceData();
            var lines = text.Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != Header) throw new FormatException("Not an FE1 file");
            for (int n = 1; n < lines.Length; n++)
            {
                var line = lines[n]; if (line.Length == 0) continue;
                var f = line.Split('|');
                switch (f[0])
                {
                    case "T": d.Targets.Add(new Target { Kind = int.Parse(f[1]), Ref = int.Parse(f[2]), Sub = int.Parse(f[3]), Rest = ParseFloats(f[4]) }); break;
                    case "X":
                        var x = new Expression { Name = Unesc(f[1]) };
                        if (f[2].Length > 0) foreach (var pair in f[2].Split(';')) { var kv = pair.Split(':'); x.Targets.Add(int.Parse(kv[0])); x.Values.Add(ParseFloats(kv[1])); }
                        d.Expressions.Add(x); break;
                    case "B": d.Branches.Add(new Branch { Base = int.Parse(f[1]), Left = int.Parse(f[2]), Right = int.Parse(f[3]), Both = int.Parse(f[4]), Flags = int.Parse(f[5]) }); break;
                    case "M":
                        var m = new Mode { Name = Unesc(f[1]), Anim = int.Parse(f[2]), Flags = int.Parse(f[3]) };
                        var tb = f[4].Split(','); for (int i = 0; i < 64; i++) m.Table[i] = int.Parse(tb[i]);
                        d.Modes.Add(m); break;
                    case "G": d.Groups.Add(new Group { Name = Unesc(f[1]), Parent = int.Parse(f[2]) }); break;
                    case "I": d.Items.Add(new Item { Group = int.Parse(f[1]), Type = f[2][0], Index = int.Parse(f[3]) }); break;
                    default: throw new FormatException("Unknown line " + f[0]);
                }
            }
            return d;
        }
    }
}
