// 两个导出工具（BlockExport / IsolatedExport）共用的读写与跨文档拷贝逻辑。
// 通过 <Compile Include="..\Shared\CadToolkit.cs" /> 链进各自项目，不单独建程序集。

using System.Reflection;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;

namespace DrawingView.Tools.Shared;

/// <summary>图纸读写 + 通用小工具。</summary>
public static class CadIo
{
    // 解析期的已知噪音（DGN 线型、标注关联等），不刷屏
    private static readonly string[] Noise =
    {
        "Entry not found", "Unlisted object", "Reactor with handle", "XRecord reference",
        "Error when trying to add the entry",
    };
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

    public static void Notify(object? sender, NotificationEventArgs e)
    {
        var msg = e.Message ?? "";
        if (Noise.Any(n => msg.Contains(n, StringComparison.OrdinalIgnoreCase))) return;
        if (!Seen.Add(msg)) return;   // 同类只报一次
        Console.WriteLine("    [通知] " + msg);
    }

    public static CadDocument Read(string path)
    {
        if (Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase))
        {
            using var r = new DwgReader(path, Notify);
            return r.Read();
        }
        using var r2 = new DxfReader(path, Notify);
        return r2.Read();
    }

    /// <summary>写文件；dwg 失败按版本回退，最后降级 dxf。返回实际写入的路径。</summary>
    public static string Write(CadDocument doc, string path, string format, ACadVersion? forceVersion)
    {
        if (format == "dxf")
        {
            var dxf = Path.ChangeExtension(path, ".dxf");
            using (var w = new DxfWriter(dxf, doc, false)) w.Write();
            return dxf;
        }

        var versions = new List<ACadVersion>();
        if (forceVersion.HasValue) versions.Add(forceVersion.Value);
        versions.Add(doc.Header.Version);
        foreach (var v in new[] { ACadVersion.AC1032, ACadVersion.AC1027, ACadVersion.AC1021, ACadVersion.AC1018, ACadVersion.AC1015 })
            if (!versions.Contains(v)) versions.Add(v);

        Exception? last = null;
        foreach (var v in versions)
        {
            try
            {
                doc.Header.Version = v;
                var dwgPath = Path.ChangeExtension(path, ".dwg");
                using (var w = new DwgWriter(dwgPath, doc)) w.Write();
                return dwgPath;
            }
            catch (Exception ex)
            {
                last = ex;
                Console.WriteLine($"       dwg({v}) 写入失败: {ex.Message}");
            }
        }

        Console.WriteLine($"       全部 DWG 版本写入失败，降级 DXF: {last?.Message}");
        var dxfPath = Path.ChangeExtension(path, ".dxf");
        using (var w = new DxfWriter(dxfPath, doc, false)) w.Write();
        return dxfPath;
    }

    public static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        s = Regex.Replace(s, @"\s+", " ");
        s = s.Trim('_', '.', ' ');   // 匿名块 "*U25" → "U25"
        if (string.IsNullOrWhiteSpace(s)) s = "block";
        if (s.Length > 80) s = s[..80];
        return s;
    }

    /// <summary>块包围盒（反射取值：BoundingBox 是值类型，装箱后才能反射）。</summary>
    public static string Bounds(BlockRecord br)
    {
        try
        {
            object b = br.GetBoundingBox();
            var t = b.GetType();
            var min = t.GetProperty("Min")?.GetValue(b);
            var max = t.GetProperty("Max")?.GetValue(b);
            return min is null || max is null ? b.ToString() ?? "-" : $"{min} ~ {max}";
        }
        catch
        {
            return "-";
        }
    }

    /// <summary>实体构成摘要（前 4 类），回读校验时比数量更能说明问题。</summary>
    public static IEnumerable<string> Kinds(IEnumerable<Entity> entities)
        => entities.GroupBy(e => e.ObjectName ?? e.GetType().Name)
                   .OrderByDescending(g => g.Count())
                   .Take(4)
                   .Select(g => $"{g.Key}×{g.Count()}");
}

/// <summary>把一批实体（含其递归依赖的块）拷进一个新的 CadDocument 模型空间。</summary>
public static class CadDocCopier
{
    public static CadDocument Build(IEnumerable<Entity> rootEntities, ACadVersion version)
    {
        var doc = new CadDocument(version);
        var ctx = new CopyContext(doc);
        foreach (var e in rootEntities)
            doc.Entities.Add(ctx.Clone(e));
        return doc;
    }

    /// <summary>把 INSERT 的属性实体换成新文档里的克隆（避免与原图共享引用）。</summary>
    private static void ReplaceAttributes(Insert dst, Insert src, CopyContext ctx)
    {
        var p = typeof(Insert).GetProperty("Attributes", BindingFlags.Public | BindingFlags.Instance);
        if (p?.GetValue(dst) is not System.Collections.IList list) return;

        list.Clear();
        foreach (var raw in src.Attributes)
        {
            if (raw is not AttributeEntity sa) continue;
            var ac = (AttributeEntity)sa.Clone();
            ac.Layer = ctx.EnsureLayer(sa.Layer);
            if (sa.LineType is not null) ac.LineType = ctx.EnsureLineType(sa.LineType);
            if (sa.Style is not null) ac.Style = ctx.EnsureStyle(sa.Style);
            list.Add(ac);
        }
    }

    private sealed class CopyContext
    {
        private readonly CadDocument _doc;
        private readonly Dictionary<string, Layer> _layers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LineType> _ltypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextStyle> _styles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DimensionStyle> _dimStyles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BlockRecord> _blocks = new(StringComparer.OrdinalIgnoreCase);

        public CopyContext(CadDocument doc) => _doc = doc;

        public Layer EnsureLayer(Layer? src)
        {
            var name = src?.Name ?? "0";
            if (_layers.TryGetValue(name, out var hit)) return hit;
            var existing = _doc.Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { _layers[name] = existing; return existing; }
            if (src is null)
            {
                var l0 = _doc.Layers.First();
                _layers[name] = l0;
                return l0;
            }
            var clone = (Layer)src.Clone();
            _doc.Layers.Add(clone);
            _layers[name] = clone;
            return clone;
        }

        public LineType EnsureLineType(LineType? src)
        {
            if (src is null) return _doc.LineTypes.First();
            var name = src.Name ?? "Continuous";
            if (_ltypes.TryGetValue(name, out var hit)) return hit;
            var existing = _doc.LineTypes.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { _ltypes[name] = existing; return existing; }
            var clone = (LineType)src.Clone();
            _doc.LineTypes.Add(clone);
            _ltypes[name] = clone;
            return clone;
        }

        public TextStyle EnsureStyle(TextStyle? src)
        {
            if (src is null) return _doc.TextStyles.First();
            var name = src.Name ?? "Standard";
            if (_styles.TryGetValue(name, out var hit)) return hit;
            var existing = _doc.TextStyles.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { _styles[name] = existing; return existing; }
            var clone = (TextStyle)src.Clone();
            _doc.TextStyles.Add(clone);
            _styles[name] = clone;
            return clone;
        }

        public DimensionStyle EnsureDimStyle(DimensionStyle? src)
        {
            if (src is null) return _doc.DimensionStyles.First();
            var name = src.Name ?? "Standard";
            if (_dimStyles.TryGetValue(name, out var hit)) return hit;
            var existing = _doc.DimensionStyles.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { _dimStyles[name] = existing; return existing; }
            var clone = (DimensionStyle)src.Clone();
            _doc.DimensionStyles.Add(clone);
            _dimStyles[name] = clone;
            return clone;
        }

        public BlockRecord EnsureBlock(BlockRecord src)
        {
            var name = src.Name ?? "";
            if (_blocks.TryGetValue(name, out var hit)) return hit;
            var existing = _doc.BlockRecords.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { _blocks[name] = existing; return existing; }

            var nb = new BlockRecord(name);
            _doc.BlockRecords.Add(nb);
            _blocks[name] = nb;

            foreach (var e in src.Entities)
                nb.Entities.Add(Clone(e));
            return nb;
        }

        public Entity Clone(Entity src)
        {
            var c = (Entity)src.Clone();

            c.Layer = EnsureLayer(src.Layer);
            if (src.LineType is not null) c.LineType = EnsureLineType(src.LineType);

            if (src is Insert si && c is Insert ci)
            {
                if (si.Block is not null)
                {
                    var nb = EnsureBlock(si.Block);
                    if (!RefSetter.TrySet(ci, "Block", nb))
                        Console.WriteLine("       [警告] Insert.Block 只读且无可写后备字段，块引用可能悬空");
                }
                ReplaceAttributes(ci, si, this);
            }
            else if (src is Dimension sd && c is Dimension cd)
            {
                if (sd.Block is not null)
                {
                    var nb = EnsureBlock(sd.Block);
                    if (!RefSetter.TrySet(cd, "Block", nb))
                        Console.WriteLine("       [警告] Dimension.Block 只读且无可写后备字段，标注块引用可能悬空");
                }
                cd.Style = EnsureDimStyle(sd.Style);
            }
            else if (src is TextEntity st && c is TextEntity ct)
            {
                ct.Style = EnsureStyle(st.Style);
            }
            else if (src is MText sm && c is MText cm)
            {
                cm.Style = EnsureStyle(sm.Style);
            }
            else if (src is AttributeDefinition sad && c is AttributeDefinition cad)
            {
                cad.Style = EnsureStyle(sad.Style);
            }

            return c;
        }
    }
}

/// <summary>写只读引用属性：优先非公开 setter，其次后备字段。</summary>
public static class RefSetter
{
    public static bool TrySet(object target, string prop, object value)
    {
        for (var t = target.GetType(); t is not null; t = t.BaseType)
        {
            var setter = t.GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                          ?.GetSetMethod(true);
            if (setter is not null)
            {
                setter.Invoke(target, new[] { value });
                return true;
            }

            foreach (var fn in new[]
                     {
                         $"<{prop}>k__BackingField",
                         "_" + prop,
                         "_" + char.ToLowerInvariant(prop[0]) + prop[1..],
                     })
            {
                var f = t.GetField(fn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f is not null)
                {
                    f.SetValue(target, value);
                    return true;
                }
            }
        }
        return false;
    }
}
