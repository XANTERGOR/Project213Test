using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace LocalTerrainPrototype
{
    internal static class LTFrameProfileReader
    {
        // Unity exposes the hierarchy reader publicly, but GPU column constants are internal.
        // Resolve the installed version's constants; never guess a numeric column or a GPU time.
        static readonly int GpuTotal = Column("columnTotalGpuTime");
        static readonly int GpuSelf = Column("columnSelfGpuTime");
        static int Column(string name)
        {
            var field = typeof(HierarchyFrameDataView).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return field != null && field.IsLiteral && field.FieldType == typeof(int) ? (int)field.GetRawConstantValue() : -1;
        }
        internal static LTFrameProfileReport.Frame Read(int index, List<string> notes)
        {
            using (var raw = ProfilerDriver.GetRawFrameDataView(index, 0))
            {
                if (!raw.valid) return null;
                var frame = new LTFrameProfileReport.Frame { index = index, cpuMs = raw.frameTimeMs };
                if (!LTFrameProfileReport.Finite(frame.cpuMs) || frame.cpuMs <= 0) return null;
                var gpuState = ProfilerDriver.GetGpuStatisticsAvailabilityState(index);
                frame.gpuStatus = gpuState.ToString();
                bool gpuValid = (gpuState & GpuProfilingStatisticsAvailabilityStates.Gathered) != 0 &&
                    LTFrameProfileReport.Finite(raw.frameGpuTimeMs) && raw.frameGpuTimeMs > 0;
                if (gpuValid) frame.gpuMs = raw.frameGpuTimeMs;
                frame.gcBytes = Counter(raw, "GC Allocated In Frame");
                frame.batches = Counter(raw, "Batches Count");
                frame.triangles = Counter(raw, "Triangles Count");
                frame.setPass = Counter(raw, "SetPass Calls Count");
                using (var iterator = new ProfilerFrameDataIterator())
                {
                    int threads = iterator.GetThreadCount(index);
                    if (threads > 256) Note(notes, "Более 256 потоков: таблица CPU ограничена первыми 256; исходный профиль сохранён отдельно.");
                    for (int thread = 0; thread < Math.Min(threads, 256); thread++)
                        ReadThread(index, thread, gpuValid, frame, notes);
                }
                if (gpuValid && (GpuTotal < 0 || GpuSelf < 0))
                    Note(notes, "Эта версия Unity не предоставляет колонки GPU-проходов; доступно только общее время GPU.");
                return frame;
            }
        }
        static long Counter(RawFrameDataView raw, string name)
        {
            int id = raw.GetMarkerId(name);
            return id >= 0 && raw.HasCounterValue(id) ? raw.GetCounterValueAsLong(id) : -1;
        }
        internal static void Note(List<string> notes, string text)
        { if (!notes.Contains(text)) notes.Add(text); }

        static void ReadThread(int frameIndex, int threadIndex, bool gpuValid, LTFrameProfileReport.Frame frame, List<string> notes)
        {
            using (var view = ProfilerDriver.GetHierarchyFrameDataView(frameIndex, threadIndex,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnDontSort, false))
            {
                if (!view.valid) return;
                string thread = view.threadName + " [" + view.threadGroupName + "; " + view.threadId + "]";
                var stack = new Stack<(int id, string parent, int depth)>();
                var children = new List<int>();
                int root = view.GetRootItemID();
                view.GetItemChildren(root, children);
                foreach (int child in children) stack.Push((child, "", 0));
                int count = 0;
                while (stack.Count > 0)
                {
                    if (++count > 250000)
                    { Note(notes, "Таблица маркеров ограничена 250000 узлами на поток/кадр; смотрите исходный профиль."); break; }
                    var item = stack.Pop();
                    string name = view.GetItemName(item.id);
                    string path = string.IsNullOrEmpty(item.parent) ? name : item.parent + "/" + name;
                    double total = view.GetItemColumnDataAsDouble(item.id, HierarchyFrameDataView.columnTotalTime);
                    double self = view.GetItemColumnDataAsDouble(item.id, HierarchyFrameDataView.columnSelfTime);
                    double calls = view.GetItemColumnDataAsDouble(item.id, HierarchyFrameDataView.columnCalls);
                    if (total > 0 || self > 0)
                        frame.samples.Add(new LTFrameProfileReport.Sample { thread = thread, path = path, name = name, total = total, self = self, calls = calls });
                    // GPU timings live on instrumented rendering samples; keep thread/path identity.
                    if (gpuValid && GpuTotal >= 0 && GpuSelf >= 0)
                    {
                        double gpuTotal = view.GetItemColumnDataAsDouble(item.id, GpuTotal);
                        double gpuSelf = view.GetItemColumnDataAsDouble(item.id, GpuSelf);
                        if (LTFrameProfileReport.Finite(gpuTotal) && gpuTotal > 0 && LTFrameProfileReport.Finite(gpuSelf) && gpuSelf >= 0)
                            frame.samples.Add(new LTFrameProfileReport.Sample { gpu = true, thread = thread, path = path, name = name, total = gpuTotal, self = gpuSelf, calls = calls });
                    }
                    if (item.depth >= 128)
                    { Note(notes, "Глубина таблицы ограничена 128 уровнями; более глубокие узлы доступны в исходном профиле."); continue; }
                    children.Clear(); view.GetItemChildren(item.id, children);
                    foreach (int child in children) stack.Push((child, path, item.depth + 1));
                }
            }
        }
    }
}
