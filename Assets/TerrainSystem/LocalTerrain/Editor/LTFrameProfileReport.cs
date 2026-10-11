using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LocalTerrainPrototype
{
    // Pure report math; no scene, Profiler or Unity dependency. All aggregation runs AFTER capture.
    internal sealed class LTFrameProfileReport
    {
        internal sealed class Sample
        {
            public string thread, path, name;
            public double total, self, calls;
            public bool gpu;
        }
        internal sealed class Frame
        {
            public int index;
            public double cpuMs, gpuMs = double.NaN;
            public long gcBytes = -1, batches = -1, triangles = -1, setPass = -1;
            public string gpuStatus;
            public readonly List<Sample> samples = new List<Sample>();
        }
        internal sealed class Row
        {
            public string thread, path, name;
            public bool gpu;
            public double total, self, calls, maxSelf, maxTotal;
            public int present, peakFrame;
        }
        public readonly List<Frame> frames = new List<Frame>();
        public readonly List<string> notes = new List<string>();
        public string context, reason;
        public int receivedFrames, missingFrames;
        public double wallSeconds;

        internal static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        internal static double TimeValue(double x) => Finite(x) && x >= 0 ? x : 0;
        internal static string Number(double x) => Finite(x) ? x.ToString("0.###", CultureInfo.InvariantCulture) : "N/A";
        internal static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        internal static double Percentile(IEnumerable<double> values, double fraction)
        {
            var sorted = values.Where(x => Finite(x) && x >= 0).OrderBy(x => x).ToArray();
            if (sorted.Length == 0) return double.NaN;
            int rank = (int)Math.Ceiling(Math.Max(0, Math.Min(1, fraction)) * sorted.Length) - 1;
            return sorted[Math.Max(0, rank)];
        }
        internal static string Stats(IEnumerable<double> values)
        {
            var valid = values.Where(x => Finite(x) && x >= 0).ToArray();
            return valid.Length == 0 ? "N/A (нет данных)" :
                Number(valid.Average()) + " / " + Number(Percentile(valid, .5)) + " / " +
                Number(Percentile(valid, .95)) + " / " + Number(valid.Max()) + " (n=" + valid.Length + ")";
        }
        // This labels known synchronization markers, NOT their underlying cause.
        internal static bool IsWait(string name) => !string.IsNullOrEmpty(name) &&
            (name.IndexOf("WaitFor", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("Semaphore.Wait", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("WaitOn", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("Gfx.Present", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name == "EditorLoop.Idle");
        internal static bool IsMain(string thread) => thread != null && thread.StartsWith("Main Thread", StringComparison.Ordinal);
        internal static bool IsRender(string thread) => thread != null && thread.StartsWith("Render Thread", StringComparison.Ordinal);

        internal List<Row> Rows()
        {
            var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (var frame in frames)
            {
                // Same marker path may occur several times per frame. Max is a FRAME sum, not one call.
                var within = new Dictionary<string, Row>(StringComparer.Ordinal);
                foreach (var sample in frame.samples)
                {
                    string key = (sample.gpu ? "G" : "C") + "\0" + sample.thread + "\0" + sample.path;
                    if (!within.TryGetValue(key, out var row))
                        within.Add(key, row = new Row { thread = sample.thread, path = sample.path, name = sample.name, gpu = sample.gpu });
                    row.total += TimeValue(sample.total); row.self += TimeValue(sample.self); row.calls += TimeValue(sample.calls);
                }
                foreach (var pair in within)
                {
                    var value = pair.Value;
                    if (!rows.TryGetValue(pair.Key, out var row))
                        rows.Add(pair.Key, row = new Row { thread = value.thread, path = value.path, name = value.name, gpu = value.gpu });
                    row.total += value.total; row.self += value.self; row.calls += value.calls; row.present++;
                    if (value.self >= row.maxSelf) { row.maxSelf = value.self; row.peakFrame = frame.index; }
                    row.maxTotal = Math.Max(row.maxTotal, value.total);
                }
            }
            return rows.Values.OrderByDescending(r => r.self).ThenBy(r => r.path, StringComparer.Ordinal).ToList();
        }
        int Denominator(Row row) => row.gpu ? frames.Count(f => Finite(f.gpuMs)) : frames.Count;
        double Mean(double sum, Row row) => Denominator(row) > 0 ? sum / Denominator(row) : double.NaN;

        public string Summary()
        {
            var rows = Rows();
            var text = new StringBuilder("FRAME FLIGHT PROFILE v1\n");
            text.AppendLine(context).AppendLine("Результат: " + reason);
            text.AppendLine("Окно записи, с: " + Number(wallSeconds) + "; получено кадров: " + receivedFrames +
                "; проанализировано полных: " + frames.Count + "; недоступно/вытеснено: " + missingFrames);
            text.AppendLine("Первый и последний полученные кадры исключены из статистики: границы записи могут быть неполными.");
            if (missingFrames > 0) text.AppendLine("ВНИМАНИЕ: неполный захват. История Profiler могла вытеснить начало; выводы относятся только к сохранившимся кадрам.");
            if (frames.Count < 10) text.AppendLine("ВНИМАНИЕ: слишком мало кадров для устойчивого вывода; повторите захват.");
            text.AppendLine("\nВремя кадра, ms: mean / median / p95 / max");
            text.AppendLine("CPU frame (включая ожидания): " + Stats(frames.Select(f => f.cpuMs)));
            text.AppendLine("GPU frame: " + Stats(frames.Select(f => f.gpuMs)));
            text.AppendLine("GC allocated bytes/frame: " + Stats(frames.Where(f => f.gcBytes >= 0).Select(f => (double)f.gcBytes)));
            text.AppendLine("Batches/frame: " + Stats(frames.Where(f => f.batches >= 0).Select(f => (double)f.batches)));
            text.AppendLine("Triangles/frame: " + Stats(frames.Where(f => f.triangles >= 0).Select(f => (double)f.triangles)));
            text.AppendLine("SetPass/frame: " + Stats(frames.Where(f => f.setPass >= 0).Select(f => (double)f.setPass)));
            text.AppendLine("GPU availability: " + string.Join("; ", frames.Select(f => f.gpuStatus).Distinct()));

            text.AppendLine("\nКак читать: Self — время без дочерних маркеров; Total — вместе с ними.");
            text.AppendLine("Вложенные Total нельзя складывать. CPU/GPU и параллельные потоки тоже не складываются в длительность кадра.");
            text.AppendLine("Ожидания НЕ доказывают GPU bottleneck: возможны VSync, лимит FPS, другой поток или простой Editor.");
            text.AppendLine("Self не является аппаратным CPU busy time: немаркированное ожидание драйвера может остаться внутри него.");
            text.AppendLine("GPU/профилировщик добавляют overhead; это замер Editor, не производительности standalone-player.");
            text.AppendLine("Все видимые окна/камеры могут участвовать в кадре. Scene View CPU frame — цикл редактора, а не чистый FPS игры.");
            if (!frames.Any(f => Finite(f.gpuMs)))
                text.AppendLine("GPU НЕДОСТУПЕН: по этому захвату нельзя назвать самый тяжёлый GPU-проход или исключить GPU bottleneck.");
            else if (!rows.Any(r => r.gpu))
                text.AppendLine("Общее время GPU доступно, но детализация GPU-проходов не получена; причину нагрузки GPU по CPU-таблице не угадываем.");
            foreach (var note in notes) text.AppendLine("NOTE: " + note);

            Table(text, "CPU Main Thread — наибольший Self, без известных ожиданий", rows.Where(r => !r.gpu && IsMain(r.thread) && !IsWait(r.name)));
            Table(text, "CPU Main Thread — ожидания отдельно", rows.Where(r => !r.gpu && IsMain(r.thread) && IsWait(r.name)));
            Table(text, "CPU Render Thread — Self (ожидания помечены)", rows.Where(r => !r.gpu && IsRender(r.thread)));
            Table(text, "CPU другие потоки — Self; параллельная работа, не вклад в critical path", rows.Where(r => !r.gpu && !IsMain(r.thread) && !IsRender(r.thread)));
            Table(text, "Local Terrain — включающее дочерние вызовы время; НЕ суммировать строки", rows.Where(r => !r.gpu && r.name.StartsWith("LT.", StringComparison.Ordinal)).OrderByDescending(r => r.total));
            Table(text, "GPU vegetation — CPU-подготовка и отправка команд (НЕ время GPU; вложенные Total не складывать)",
                rows.Where(r => !r.gpu && IsMain(r.thread) && r.name.StartsWith("LT.Details.GPU.Stage.", StringComparison.Ordinal)).OrderByDescending(r => r.total),64);
            Table(text, "GPU — измеренные проходы (не оценка по CPU)", rows.Where(r => r.gpu));

            text.AppendLine("\nСамые долгие CPU-кадры и их верхние Self-маркеры:");
            foreach (var frame in frames.OrderByDescending(f => f.cpuMs).Take(8))
            {
                text.AppendLine("Frame " + frame.index + ": CPU " + Number(frame.cpuMs) + " ms; GPU " + Number(frame.gpuMs) + " ms");
                foreach (var sample in frame.samples.Where(s => !s.gpu && IsMain(s.thread)).OrderByDescending(s => s.self).Take(5))
                    text.AppendLine("  " + Number(sample.self) + " ms Self | " + (IsWait(sample.name) ? "WAIT | " : "") + sample.path);
            }
            return text.ToString();
        }
        void Table(StringBuilder text, string title, IEnumerable<Row> source,int limit=20)
        {
            text.AppendLine("\n" + title).AppendLine("mean Self ms | mean Total ms | max Self ms (frame) | calls/frame | thread | path");
            var selected = source.Take(limit).ToArray();
            if (selected.Length == 0) text.AppendLine("Нет зарегистрированных замеров.");
            foreach (var row in selected)
                text.AppendLine(Number(Mean(row.self, row)) + " | " + Number(Mean(row.total, row)) + " | " + Number(row.maxSelf) +
                    " (#" + row.peakFrame + ") | " + Number(Mean(row.calls, row)) + " | " + row.thread + " | " +
                    (IsWait(row.name) && !row.gpu ? "[WAIT] " : "") + row.path);
        }
        public string FramesCsv()
        {
            var text = new StringBuilder("frame,cpu_ms_including_waits,gpu_ms,gc_alloc_bytes,batches,triangles,setpass,gpu_availability\n");
            foreach (var f in frames)
                text.AppendLine(f.index + "," + Number(f.cpuMs) + "," + Number(f.gpuMs) + "," + Counter(f.gcBytes) + "," +
                    Counter(f.batches) + "," + Counter(f.triangles) + "," + Counter(f.setPass) + "," + Csv(f.gpuStatus));
            return text.ToString();
        }
        static string Counter(long value) => value < 0 ? "N/A" : value.ToString(CultureInfo.InvariantCulture);
        public string MarkersCsv()
        {
            var text = new StringBuilder("domain,thread,path,mean_self_ms,mean_total_ms,max_frame_self_ms,peak_frame,max_frame_total_ms,calls,frames_present,denominator_frames,known_wait\n");
            foreach (var r in Rows())
                text.AppendLine((r.gpu ? "GPU" : "CPU") + "," + Csv(r.thread) + "," + Csv(r.path) + "," +
                    Number(Mean(r.self, r)) + "," + Number(Mean(r.total, r)) + "," + Number(r.maxSelf) + "," +
                    r.peakFrame + "," + Number(r.maxTotal) + "," + Number(r.calls) + "," + r.present + "," +
                    Denominator(r) + "," + (!r.gpu && IsWait(r.name) ? "true" : "false"));
            return text.ToString();
        }
    }
}
