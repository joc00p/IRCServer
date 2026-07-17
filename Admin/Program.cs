using IRCServer.Admin;

namespace IRCServer.Admin;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new AdminForm());
    }
}
