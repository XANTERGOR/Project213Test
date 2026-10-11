// Standalone numeric harness: no Unity project/import/scene or native Jobs.
static class LTFourStagesMonoCheck
{
    static int Main(){FourStageMathChecks.Run();return 0;}
}
// Only the frozen pre-cooperative oracle uses this UI API. Its numeric work is
// unchanged; a standalone process cannot display an Editor progress window.
namespace UnityEditor
{
    static class EditorUtility
    {
        public static bool DisplayCancelableProgressBar(string title,string message,float progress)=>false;
        public static void ClearProgressBar(){}
    }
}
