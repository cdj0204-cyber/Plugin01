using Rhino;
using Rhino.Commands;
using Rhino.Geometry;

namespace Plugin01
{
    public class Plugin01Command : Command
    {
        public Plugin01Command()
        {
            Instance = this;
        }

        public static Plugin01Command Instance { get; private set; }

        // Rhino 명령행에 입력할 명령어 이름
        public override string EnglishName => "Plugin01Hello";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            RhinoApp.WriteLine("Hello from Plugin 01! (Rhino {0})", RhinoApp.Version);

            // 예시: 원점에 점 하나 추가
            doc.Objects.AddPoint(Point3d.Origin);
            doc.Views.Redraw();

            return Result.Success;
        }
    }
}
