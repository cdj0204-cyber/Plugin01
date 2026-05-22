using Rhino.PlugIns;

namespace Plugin01
{
    public class Plugin01Plugin : PlugIn
    {
        public Plugin01Plugin()
        {
            Instance = this;
        }

        public static Plugin01Plugin Instance { get; private set; }

        // 시작 시 로드 -> 새 명령이 즉시 인식됨 (on-demand 캐시 문제 방지)
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
    }
}
