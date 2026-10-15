using System;
using System.Windows.Forms;

namespace BfbbOnline.Client;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bot") return BotMode.Run(args);
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
