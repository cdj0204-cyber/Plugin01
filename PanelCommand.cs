using Rhino;
using Rhino.Commands;

namespace Plugin01
{
    public class PanelCommand : Command
    {
        public PanelCommand()
        {
            Instance = this;
        }

        public static PanelCommand Instance { get; private set; }

        public override string EnglishName => "Plugin01Panel";

        private static Plugin01Panel _form;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (_form == null)
            {
                _form = new Plugin01Panel();
                _form.Closed += (s, e) => _form = null;
                _form.Show();
            }
            else
            {
                _form.BringToFront();
            }
            return Result.Success;
        }
    }
}
