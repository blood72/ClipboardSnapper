using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

// A real delayed clipboard owner lets UI tests hold acquisition without changing the app.
public static class DelayedClipboard
{
    private static readonly ManualResetEvent Ready = new ManualResetEvent(false);
    private static readonly ManualResetEvent Release = new ManualResetEvent(false);
    private static Thread thread;
    private static Control control;
    private static ApplicationContext context;
    private static Exception error;

    public static void Start()
    {
        Ready.Reset(); Release.Reset(); error = null;
        thread = new Thread(() =>
        {
            try
            {
                using (var bitmap = new Bitmap(32, 24))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var owner = new Control())
                using (var loop = new ApplicationContext())
                {
                    graphics.Clear(Color.CornflowerBlue);
                    control = owner; context = loop;
                    var handle = owner.Handle;
                    var data = new DataObject();
                    data.SetImage(bitmap);
                    Clipboard.SetDataObject(new DelayedData(data), false);
                    Ready.Set();
                    Application.Run(loop);
                }
            }
            catch (Exception exception) { error = exception; Ready.Set(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!Ready.WaitOne(5000)) throw new TimeoutException("Delayed clipboard owner did not start.");
        if (error != null) throw new InvalidOperationException("Delayed clipboard setup failed.", error);
    }

    public static void Complete() { Release.Set(); }
    public static void Stop()
    {
        Release.Set();
        if (control != null && !control.IsDisposed)
            control.BeginInvoke(new Action(() => context.ExitThread()));
        if (thread != null && !thread.Join(5000)) throw new TimeoutException("Delayed clipboard owner did not exit.");
        control = null; context = null; thread = null;
    }

    private sealed class DelayedData : IDataObject
    {
        private readonly DataObject data;
        public DelayedData(DataObject data) { this.data = data; }
        public object GetData(string format, bool autoConvert)
        {
            if (!Release.WaitOne(15000)) throw new TimeoutException("UI test did not release the clipboard image.");
            return data.GetData(format, autoConvert);
        }
        public object GetData(string format) { return GetData(format, true); }
        public object GetData(Type format) { return GetData(format.FullName, true); }
        public bool GetDataPresent(string format, bool autoConvert) { return data.GetDataPresent(format, autoConvert); }
        public bool GetDataPresent(string format) { return data.GetDataPresent(format); }
        public bool GetDataPresent(Type format) { return data.GetDataPresent(format); }
        public string[] GetFormats(bool autoConvert) { return data.GetFormats(autoConvert); }
        public string[] GetFormats() { return data.GetFormats(); }
        public void SetData(string format, bool autoConvert, object value) { data.SetData(format, autoConvert, value); }
        public void SetData(string format, object value) { data.SetData(format, value); }
        public void SetData(Type format, object value) { data.SetData(format, value); }
        public void SetData(object value) { data.SetData(value); }
    }
}
