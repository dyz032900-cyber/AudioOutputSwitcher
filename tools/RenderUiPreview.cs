using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;

internal static class RenderUiPreview
{
    private static object settingsObject;
    private static object devicesObject;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: RenderUiPreview.exe <assembly-path> <output-dir>");
                return 2;
            }

            string assemblyPath = Path.GetFullPath(args[0]);
            string outputDir = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(outputDir);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            Type appSettingsType = assembly.GetType("AudioOutputSwitcher.AppSettings", true);
            Type audioDeviceType = assembly.GetType("AudioOutputSwitcher.AudioDeviceInfo", true);
            Type dashboardType = assembly.GetType("AudioOutputSwitcher.DashboardForm", true);
            Type hotkeySettingsType = assembly.GetType("AudioOutputSwitcher.HotkeySettingsForm", true);
            Type captureType = assembly.GetType("AudioOutputSwitcher.HotkeyCaptureForm", true);

            settingsObject = CreateSampleSettings(assembly, appSettingsType);
            devicesObject = CreateSampleDevices(audioDeviceType);

            Form dashboard = CreateDashboardForm(dashboardType, appSettingsType, audioDeviceType);
            MethodInfo refreshData = dashboardType.GetMethod("RefreshData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (refreshData != null)
            {
                refreshData.Invoke(dashboard, null);
            }
            RenderForm(dashboard, Path.Combine(outputDir, "dashboard-preview.png"));

            ConstructorInfo hotkeyCtor = hotkeySettingsType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new Type[] { appSettingsType, devicesObject.GetType() },
                null);
            Form hotkeySettings = (Form)hotkeyCtor.Invoke(new object[] { settingsObject, devicesObject });
            RenderForm(hotkeySettings, Path.Combine(outputDir, "hotkey-settings-preview.png"));

            Form capture = (Form)Activator.CreateInstance(captureType, true);
            RenderForm(capture, Path.Combine(outputDir, "capture-preview.png"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().FullName);
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.Error.WriteLine("INNER: " + ex.InnerException.GetType().FullName);
                Console.Error.WriteLine(ex.InnerException.Message);
                Console.Error.WriteLine(ex.InnerException.StackTrace);
            }
            return 1;
        }
    }

    private static Form CreateDashboardForm(Type dashboardType, Type appSettingsType, Type audioDeviceType)
    {
        Type devicesListType = typeof(List<>).MakeGenericType(audioDeviceType);
        Type getSettingsType = typeof(Func<>).MakeGenericType(appSettingsType);
        Type getDevicesType = typeof(Func<>).MakeGenericType(devicesListType);
        Type switchDeviceType = typeof(Action<>).MakeGenericType(audioDeviceType);

        Delegate getSettings = CreateBoxedFuncDelegate(getSettingsType, appSettingsType, typeof(RenderUiPreview).GetMethod("GetSettingsBoxed", BindingFlags.Static | BindingFlags.NonPublic));
        Delegate getDevices = CreateBoxedFuncDelegate(getDevicesType, devicesListType, typeof(RenderUiPreview).GetMethod("GetDevicesBoxed", BindingFlags.Static | BindingFlags.NonPublic));
        Delegate switchDevice = CreateBoxedActionDelegate(switchDeviceType, audioDeviceType, typeof(RenderUiPreview).GetMethod("SwitchDeviceBoxed", BindingFlags.Static | BindingFlags.NonPublic));
        Delegate openHotkeySettings = Delegate.CreateDelegate(typeof(Action), typeof(RenderUiPreview).GetMethod("OpenHotkeySettings", BindingFlags.Static | BindingFlags.NonPublic));
        Delegate afterStartupChanged = Delegate.CreateDelegate(typeof(Action), typeof(RenderUiPreview).GetMethod("AfterStartupChanged", BindingFlags.Static | BindingFlags.NonPublic));

        ConstructorInfo ctor = dashboardType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new Type[] { getSettingsType, getDevicesType, switchDeviceType, typeof(Action), typeof(Action) },
            null);

        return (Form)ctor.Invoke(new object[] { getSettings, getDevices, switchDevice, openHotkeySettings, afterStartupChanged });
    }

    private static object CreateSampleSettings(Assembly assembly, Type appSettingsType)
    {
        object settings = Activator.CreateInstance(appSettingsType, true);
        appSettingsType.GetField("ShowBalloon").SetValue(settings, true);

        object nextAction = CreateAction(assembly, "Next", null);
        object prevAction = CreateAction(assembly, "Previous", null);
        object headsetAction = CreateAction(assembly, "Name:Headset", null);

        object nextHotkey = ParseHotkey(assembly, "Ctrl+Alt+F12");
        object prevHotkey = ParseHotkey(assembly, "Ctrl+MouseX2");
        object headsetHotkey = ParseHotkey(assembly, "Shift+MouseX1");

        IList hotkeys = (IList)appSettingsType.GetField("Hotkeys").GetValue(settings);
        hotkeys.Add(CreateBinding(assembly, "Next", nextAction, nextHotkey));
        hotkeys.Add(CreateBinding(assembly, "Previous", prevAction, prevHotkey));
        hotkeys.Add(CreateBinding(assembly, "Name:Headset", headsetAction, headsetHotkey));
        return settings;
    }

    private static object CreateBinding(Assembly assembly, string sourceName, object action, object hotkey)
    {
        Type bindingType = assembly.GetType("AudioOutputSwitcher.HotkeyBinding", true);
        object binding = Activator.CreateInstance(bindingType, true);
        bindingType.GetField("SourceName").SetValue(binding, sourceName);
        bindingType.GetField("Action").SetValue(binding, action);
        bindingType.GetField("Hotkey").SetValue(binding, hotkey);
        return binding;
    }

    private static object CreateAction(Assembly assembly, string source, string fallbackName)
    {
        Type actionType = assembly.GetType("AudioOutputSwitcher.HotkeyAction", true);
        MethodInfo tryParse = actionType.GetMethod("TryParse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        object[] args = new object[] { source, null };
        bool success = (bool)tryParse.Invoke(null, args);
        if (success)
        {
            return args[1];
        }

        object action = Activator.CreateInstance(actionType, true);
        Type kindType = assembly.GetType("AudioOutputSwitcher.HotkeyActionKind", true);
        actionType.GetField("Kind").SetValue(action, Enum.Parse(kindType, "DeviceNameContains"));
        actionType.GetField("NameContains").SetValue(action, fallbackName ?? "Headset");
        return action;
    }

    private static object ParseHotkey(Assembly assembly, string text)
    {
        Type hotkeyType = assembly.GetType("AudioOutputSwitcher.HotkeyDefinition", true);
        MethodInfo tryParse = hotkeyType.GetMethod("TryParse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        object[] args = new object[] { text, null };
        bool success = (bool)tryParse.Invoke(null, args);
        if (!success)
        {
            throw new InvalidOperationException("Could not parse sample hotkey: " + text);
        }
        return args[1];
    }

    private static object CreateSampleDevices(Type audioDeviceType)
    {
        Type listType = typeof(List<>).MakeGenericType(audioDeviceType);
        IList list = (IList)Activator.CreateInstance(listType);
        list.Add(CreateDevice(audioDeviceType, "spk-primary", "Speakers - Realtek Audio", false));
        list.Add(CreateDevice(audioDeviceType, "headset-main", "Headset - Logitech G Pro X", true));
        list.Add(CreateDevice(audioDeviceType, "monitor-hdmi", "LG ULTRAGEAR - NVIDIA High Definition Audio", false));
        list.Add(CreateDevice(audioDeviceType, "usb-dac", "USB DAC - FiiO K11", false));
        return list;
    }

    private static object CreateDevice(Type audioDeviceType, string id, string name, bool isDefault)
    {
        object device = Activator.CreateInstance(audioDeviceType, true);
        audioDeviceType.GetField("Id").SetValue(device, id);
        audioDeviceType.GetField("Name").SetValue(device, name);
        audioDeviceType.GetField("IsDefault").SetValue(device, isDefault);
        return device;
    }

    private static void RenderForm(Form form, string outputPath)
    {
        using (form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            form.Refresh();
            Application.DoEvents();

            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(outputPath);
            }

            form.Hide();
        }
    }

    private static object GetSettingsBoxed()
    {
        return settingsObject;
    }

    private static object GetDevicesBoxed()
    {
        return devicesObject;
    }

    private static void SwitchDeviceBoxed(object device)
    {
    }

    private static void OpenHotkeySettings()
    {
    }

    private static void AfterStartupChanged()
    {
    }

    private static Delegate CreateBoxedFuncDelegate(Type delegateType, Type returnType, MethodInfo sourceMethod)
    {
        DynamicMethod method = new DynamicMethod("boxedFunc_" + returnType.Name, returnType, Type.EmptyTypes, typeof(RenderUiPreview).Module, true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Call, sourceMethod);
        il.Emit(OpCodes.Castclass, returnType);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate(delegateType);
    }

    private static Delegate CreateBoxedActionDelegate(Type delegateType, Type parameterType, MethodInfo sourceMethod)
    {
        DynamicMethod method = new DynamicMethod("boxedAction_" + parameterType.Name, typeof(void), new Type[] { parameterType }, typeof(RenderUiPreview).Module, true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        if (parameterType.IsValueType)
        {
            il.Emit(OpCodes.Box, parameterType);
        }
        il.Emit(OpCodes.Call, sourceMethod);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate(delegateType);
    }
}
