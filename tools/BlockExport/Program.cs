// BlockExport —— 把 DWG/DXF 里的每个「块定义」导出成独立的 DWG 文件（WBLOCK 语义）。
//
// 用法：
//   dotnet run --project tools/BlockExport -- <源图.dwg|dxf> [输出目录] [选项]
//
// 选项：
//   --list                只列出块清单（不导出）
//   --referenced          只导出模型空间 INSERT 实际引用到的块（含匿名块，递归展开嵌套）
//   --format dwg|dxf      输出格式，默认 dwg（dwg 写失败自动降级 dxf）
//   --include-anon        包含匿名块（*U / *D / *T 等）与动态块匿名定义
//   --include-empty       包含 0 实体的空块（默认跳过）
//   --fresh-read          每导出一个块都重新读一次源图（彻底隔离克隆副作用，慢）
//   --force               候选块超过 300 个时仍需导出（保险线）
//   --version AC1018      强制输出 DWG 版本（AC1015/AC1018/AC1021/AC1024/AC1027/AC1032）
//   --only <关键字>       只导出名称包含关键字的块（可重复）
//   --no-verify           写完后不回读校验
//
// 日志：设 BLOCKEXPORT_LOG=<路径> 可同时写一份 UTF-8 日志文件（Windows 控制台重定向会乱码）。

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadViewer.Tools.Shared;

namespace CadViewer.Tools.BlockExport;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var logPath = Environment.GetEnvironmentVariable("BLOCKEXPORT_LOG");
        StreamWriter? logFile = null;
        if (!string.IsNullOrEmpty(logPath))
        {
            logFile = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(logFile);
            Console.SetError(logFile);
        }

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("致命错误: " + ex);
            return 2;
        }
        finally
        {
            logFile?.Flush();
            logFile?.Dispose();
        }
    }

    private static int Run(string[] args)
    {
        string? src = null;
        string? outDir = null;
        var listOnly = false;
        var referencedOnly = false;
        var format = "dwg";
        var includeAnon = false;
        var includeEmpty = false;
        var freshRead = false;
        var allowMassExport = false;
        var verify = true;
        ACadVersion? forceVersion = null;
        var only = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--list": listOnly = true; break;
                case "--referenced": referencedOnly = true; break;
                case "--include-anon": includeAnon = true; break;
                case "--include-empty": includeEmpty = true; break;
                case "--fresh-read": freshRead = true; break;
                case "--force": allowMassExport = true; break;
                case "--no-verify": verify = false; break;
                case "--format": format = Next(args, ref i); break;
                case "--version":
                    var v = Next(args, ref i);
                    if (!Enum.TryParse(v, true, out ACadVersion parsed))
                    {
                        Console.WriteLine($"无法识别的版本 '{v}'，忽略");
                    }
                    else forceVersion = parsed;
                    break;
                case "--only": only.Add(Next(args, ref i)); break;
                default:
                    if (src is null) src = a;
                    else if (outDir is null) outDir = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(src))
        {
            Console.WriteLine("用法: BlockExport <源图.dwg|dxf> [输出目录] [选项]");
            return 1;
        }
        if (!File.Exists(src))
        {
            Console.WriteLine($"文件不存在: {src}");
            return 1;
        }
        outDir ??= Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"源文件: {src}");
        Console.WriteLine($"输出目录: {outDir}");
        Console.WriteLine();

        var doc = CadIo.Read(src);
        Console.WriteLine($"版本: {doc.Header.Version}   实体(模型空间): {doc.Entities.Count()}   块定义: {doc.BlockRecords.Count()}");
        Console.WriteLine();

        // ---- 候选块 ----
        var all = doc.BlockRecords.ToList();

        // --referenced：模型空间 INSERT 递归引用到的块（含匿名块）
        // 注意：只跟 INSERT，不跟 Dimension.Block——尺寸标注的匿名块（*D）是标注的内部实现，
        // 5330 个标注会带出 5330 个块。它们仍会作为「被导出块的内部依赖」随块一起拷贝。
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (referencedOnly)
        {
            void Mark(BlockRecord br)
            {
                if (!referenced.Add(br.Name ?? "")) return;
                foreach (var ins in br.Entities.OfType<Insert>())
                    if (ins.Block is BlockRecord dep) Mark(dep);
            }
            foreach (var ins in doc.Entities.OfType<Insert>())
                if (ins.Block is BlockRecord b) Mark(b);
        }

        // 模型空间里每个块被直接 INSERT 的次数（清单用）
        var insertCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ins in doc.Entities.OfType<Insert>())
        {
            var n = ins.Block?.Name ?? "";
            if (n.Length == 0) continue;
            insertCount[n] = insertCount.TryGetValue(n, out var c) ? c + 1 : 1;
        }

        var candidates = new List<BlockRecord>();
        foreach (var br in all)
        {
            var name = br.Name ?? "";
            if (name.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;
            if (referencedOnly)
            {
                if (!referenced.Contains(name)) continue;
            }
            else if (name.StartsWith("*", StringComparison.Ordinal) && !includeAnon)
            {
                // 匿名块、动态块匿名定义、标注匿名块
                continue;
            }
            if (br.Entities.Count == 0 && !includeEmpty) continue;
            if (only.Count > 0 && !only.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            candidates.Add(br);
        }

        Console.WriteLine($"候选块（{candidates.Count}）:");
        var shown = 0;
        foreach (var br in candidates)
        {
            if (shown++ >= 60 && candidates.Count > 80)
            {
                Console.WriteLine($"  … 其余 {candidates.Count - shown} 个省略");
                break;
            }
            var types = br.Entities.GroupBy(e => e.ObjectName ?? e.GetType().Name)
                                   .OrderByDescending(g => g.Count())
                                   .Take(4)
                                   .Select(g => $"{g.Key}×{g.Count()}");
            Console.WriteLine($"  {br.Name,-40} 实体 {br.Entities.Count,6}  依赖 {DependencyClosure(br, all).Count - 1}  [{string.Join(", ", types)}]");
        }

        if (candidates.Count > 300 && !allowMassExport)
        {
            Console.WriteLine();
            Console.WriteLine($"候选块 {candidates.Count} 个，超过 300 个的保险线，已中止。确认要导出请加 --force。");
            return 1;
        }

        var notExported = all.Where(br => !candidates.Contains(br)).ToList();
        var skippedAnon = notExported.Count(br => (br.Name ?? "").StartsWith("*", StringComparison.Ordinal));
        var skippedEmpty = notExported.Count(br => br.Entities.Count == 0 && !(br.Name ?? "").StartsWith("*", StringComparison.Ordinal));
        if (skippedAnon > 0) Console.WriteLine($"  （跳过匿名/空间块 {skippedAnon} 个，用 --include-anon 可导出）");
        if (skippedEmpty > 0) Console.WriteLine($"  （跳过空块 {skippedEmpty} 个）");

        if (notExported.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("被跳过的块按前缀分组:");
            foreach (var g in notExported.GroupBy(br => Prefix(br.Name ?? "")).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Key,-14} {g.Count(),6} 个   最大实体数 {g.Max(b => b.Entities.Count)}");
        }

        Console.WriteLine();
        Console.WriteLine("模型空间（根层）实体构成:");
        foreach (var g in doc.Entities.GroupBy(e => e.ObjectName ?? e.GetType().Name).OrderByDescending(g => g.Count()).Take(10))
            Console.WriteLine($"  {g.Key,-16} {g.Count(),7}");

        var inserts = doc.Entities.OfType<Insert>().ToList();
        if (inserts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("模型空间 INSERT 引用最多的块:");
            foreach (var g in inserts.Where(i => i.Block is not null).GroupBy(i => i.Block!.Name).OrderByDescending(g => g.Count()).Take(15))
                Console.WriteLine($"  {g.Key,-50} ×{g.Count()}");
        }

        if (listOnly) return 0;
        Console.WriteLine();

        // ---- 导出 ----
        var ok = 0; var failed = 0; var skipped = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifest = new List<(string File, string Block, int Entities, int Inserts, string Status)>();

        for (var idx = 0; idx < candidates.Count; idx++)
        {
            var br = candidates[idx];
            var safe = CadIo.SafeFileName(br.Name ?? $"block_{idx}");
            var final = safe;
            var dup = 2;
            while (!usedNames.Add(final)) final = $"{safe}_{dup++}";

            var outPath = Path.Combine(outDir, final + "." + (format == "dxf" ? "dxf" : "dwg"));

            var working = freshRead ? CadIo.Read(src) : doc;
            var target = freshRead
                ? working.BlockRecords.FirstOrDefault(b => string.Equals(b.Name, br.Name, StringComparison.OrdinalIgnoreCase))
                : br;
            if (target is null)
            {
                Console.WriteLine($"[跳过] {br.Name}: 重新读取后找不到该块");
                skipped++;
                continue;
            }

            Console.WriteLine($"[{idx + 1}/{candidates.Count}] {br.Name} → {Path.GetFileName(outPath)}");
            var insertsOf = insertCount.TryGetValue(br.Name ?? "", out var ic) ? ic : 0;
            try
            {
                var outDoc = CadDocCopier.Build(target.Entities, forceVersion ?? working.Header.Version);
                var actualPath = CadIo.Write(outDoc, outPath, format, forceVersion);
                var expected = target.Entities.Count;
                var status = "OK";

                if (verify)
                {
                    var back = CadIo.Read(actualPath);
                    var got = back.Entities.Count();
                    var srcKinds = string.Join("/", CadIo.Kinds(target.Entities));
                    var gotKinds = string.Join("/", CadIo.Kinds(back.Entities));
                    var same = got == expected && srcKinds == gotKinds;
                    status = same ? "OK" : $"差异(实体 {got}/{expected})";
                    Console.WriteLine($"       {(same ? "OK  " : "差异")} 写入 {new FileInfo(actualPath).Length / 1024.0:F1} KB，回读实体 {got}/{expected}，块定义 {back.BlockRecords.Count()}，构成 [{gotKinds}]");
                    if (!same) Console.WriteLine($"            源块构成 [{srcKinds}]");

                    var sb = CadIo.Bounds(target);
                    var db = CadIo.Bounds(back.ModelSpace);
                    Console.WriteLine($"       包围盒 源块 {sb} / 导出 {db}{(sb == db ? "" : "   ← 不一致")}");
                    if (sb != db && sb != "-" && db != "-") status += "|包围盒不一致";
                }
                else
                {
                    Console.WriteLine($"       写入 {new FileInfo(actualPath).Length / 1024.0:F1} KB");
                }
                if (!string.Equals(actualPath, outPath, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"       （{format} 写入失败，已降级为 {Path.GetExtension(actualPath).TrimStart('.')}）");
                ok++;
                manifest.Add((Path.GetFileName(actualPath), br.Name ?? "", expected, insertsOf, status));
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"       [失败] {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException is not null) Console.WriteLine($"              内部: {ex.InnerException.Message}");
                manifest.Add((Path.GetFileName(outPath), br.Name ?? "", br.Entities.Count, insertsOf, "失败: " + ex.Message));
            }
        }

        // ---- 清单 ----
        var mp = Path.Combine(outDir, "_块导出清单.csv");
        using (var mw = new StreamWriter(mp, false, new UTF8Encoding(true)))
        {
            mw.WriteLine("文件名,块名,实体数,模型空间引用次数,状态");
            foreach (var r in manifest)
                mw.WriteLine($"{CadIo.Csv(r.File)},{CadIo.Csv(r.Block)},{r.Entities},{r.Inserts},{CadIo.Csv(r.Status)}");
        }
        Console.WriteLine($"清单: {mp}");

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"完成：成功 {ok}，失败 {failed}，跳过 {skipped}，耗时 {sw.Elapsed.TotalSeconds:F1}s");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>块自身 + 它递归引用的所有块（依赖在前）。</summary>
    private static string Next(string[] args, ref int i)
        => i + 1 < args.Length ? args[++i] : "";

    private static string Prefix(string name)
    {
        if (name.Length == 0) return "(空)";
        if (name.StartsWith("*", StringComparison.Ordinal))
            return name.Length >= 2 ? name[..2] + "…" : "*";
        return name.Length > 14 ? name[..14] + "…" : name;
    }

    private static List<BlockRecord> DependencyClosure(BlockRecord root, IEnumerable<BlockRecord> pool)
    {
        var byName = new Dictionary<string, BlockRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in pool) byName[b.Name ?? ""] = b;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<BlockRecord>();
        void Walk(BlockRecord br)
        {
            if (!visited.Add(br.Name ?? "")) return;
            foreach (var e in br.Entities)
            {
                var dep = ReferencedBlock(e);
                if (dep is not null && byName.TryGetValue(dep.Name ?? "", out var nb)) Walk(nb);
            }
            order.Add(br);
        }
        Walk(root);
        return order;
    }

    internal static BlockRecord? ReferencedBlock(Entity e) => e switch
    {
        Insert ins => ins.Block,
        Dimension dim => dim.Block,
        _ => null,
    };

}
