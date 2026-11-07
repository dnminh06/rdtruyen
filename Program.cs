using System;
using System.Windows.Forms;

namespace TruyenCuaToi;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
