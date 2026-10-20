using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.UI;

class PanelPreview
{
    [STAThread] static int Main(string[] args)
    {
        if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.dat")))
            throw new InvalidOperationException("PanelPreview requires portable.dat beside the test executable.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var app = new AppHost(); app.Init();
        using (var panel = (PanelForm)Activator.CreateInstance(typeof(PanelForm), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { app }, null))
        {
            panel.Show();
            Application.DoEvents();
            var method = typeof(PanelForm).GetMethod("ShowTab", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(panel, new object[] { "settings" });
            Application.DoEvents();
            using (var image = new Bitmap(panel.Width, panel.Height))
            {
                panel.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                image.Save(args[0]);
            }
            panel.Dispose();
        }
        return 0;
    }
}
