// RanHostDriver.cs -- the Unity side of "C++ draws, Unity hosts".
//
// The native library (libranclient.so, Assets/Plugins/Android/libs/arm64-v8a)
// is the ENTIRE original client -- game logic, in-process server (emulator
// boot, fast-test path), and the D3D9->GLES3 renderer. This driver's whole job
// per frame is:
//
//   1. feed the frame delta and input to the library (game thread),
//   2. issue one plugin event so the engine runs boot/sim/render on Unity's
//      RENDER thread, where the GLES3 context is current,
//   3. draw the engine's FBO colour texture to the screen.
//
// PLAYER SETTINGS this project is already configured for:
//   - Graphics API: OpenGLES3 only (the engine shares Unity's GL context;
//     under Vulkan there is no GL context to share)
//   - IL2CPP, ARM64 only
//
// GAME DATA: the engine reads the client's data tree (data/, textures/,
// *.wld ...) from DataRoot. For fast testing push it once with adb:
//   adb push "D:\Program Files\RAN Online PH old\." \
//       /sdcard/Android/data/<package>/files/RanData
// (persistentDataPath needs no storage permission.)

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public sealed class RanHostDriver : MonoBehaviour
{
    [Tooltip("Game-data root. Empty = <persistentDataPath>/RanData")]
    public string DataRootOverride = "";

    [Tooltip("Engine render size. 0 = screen size at startup.")]
    public int RenderWidth = 0;
    public int RenderHeight = 0;

#if UNITY_ANDROID && !UNITY_EDITOR
    const string LIB = "ranclient";

    [DllImport(LIB)] static extern void   Ran_Host_Configure(string dataRoot, int width, int height);
    [DllImport(LIB)] static extern void   Ran_Host_SetDelta(float dt);
    [DllImport(LIB)] static extern IntPtr Ran_Host_GetRenderEventFunc();
    [DllImport(LIB)] static extern uint   Ran_Host_GetFrameTexture();
    [DllImport(LIB)] static extern int    Ran_Host_IsBooted();

    //	Input path from ran_api.cpp: raw held state; the shim derives the
    //	DOWN/PRESSED/UP edges the engine's state machine expects.
    [DllImport(LIB)] static extern void   Ran_SetInput(int mouseX, int mouseY, int lHeld, int rHeld, int mHeld);

    const int kEventFrame = 1;

    IntPtr    _renderEvent;
    Texture2D _frameTex;      // wraps the engine's FBO colour texture
    int       _texW, _texH;
    bool      _dataPresent;   // gate: booting without data null-derefs in CreatePC
    string    _root;

    void Awake()
    {
        //	Landscape MMO on a phone; also stops Unity re-creating the GL
        //	surface mid-run for rotations, which would orphan the engine's FBO.
        Screen.orientation = ScreenOrientation.LandscapeLeft;
        Application.targetFrameRate = 60;

        _root = string.IsNullOrEmpty(DataRootOverride)
            ? Path.Combine(Application.persistentDataPath, "RanData")
            : DataRootOverride;

        _texW = RenderWidth  > 0 ? RenderWidth  : Screen.width;
        _texH = RenderHeight > 0 ? RenderHeight : Screen.height;

        Ran_Host_Configure(_root, _texW, _texH);
        _renderEvent = Ran_Host_GetRenderEventFunc();

        //	Case-insensitive on purpose: the pushed client may spell it data/
        //	or Data/, and this check must agree with the engine's resolver.
        RefreshDataPresent();
        Debug.Log($"[RanHost] data root: {_root}  target {_texW}x{_texH}  " +
                  $"data present: {_dataPresent}");
    }

    void RefreshDataPresent()
    {
        try
        {
            if (!Directory.Exists(_root)) { _dataPresent = false; return; }
            foreach (string d in Directory.GetDirectories(_root))
                if (string.Equals(Path.GetFileName(d), "data",
                                  StringComparison.OrdinalIgnoreCase))
                { _dataPresent = true; return; }
            _dataPresent = false;
        }
        catch { _dataPresent = false; }
    }

    void Update()
    {
        //	HARD GATE. Booting without the client data reaches CreatePC with
        //	0 maps and null-derefs (measured on device, 2026-08-07). Waiting
        //	here also lets an adb push finish while the app sits open.
        if (!_dataPresent)
        {
            if (Time.frameCount % 120 == 0) RefreshDataPresent();
            return;
        }

        Ran_Host_SetDelta(Time.unscaledDeltaTime);

        //	First finger = the mouse. The engine's own UI hit-tests against
        //	this, so coordinates are scaled from screen to the engine's frame.
        if (Input.touchCount > 0)
        {
            Touch t = Input.GetTouch(0);
            int mx = (int)(t.position.x * _texW / Screen.width);
            //	Unity touch origin is bottom-left; the engine is top-left.
            int my = (int)((Screen.height - t.position.y) * _texH / Screen.height);
            bool held = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
            Ran_SetInput(mx, my, held ? 1 : 0, 0, 0);
        }
        else
        {
            Ran_SetInput(0, 0, 0, 0, 0);
        }

        //	One engine frame, on the render thread, this frame.
        GL.IssuePluginEvent(_renderEvent, kEventFrame);

        //	Wrap the FBO texture once it exists. CreateExternalTexture does not
        //	copy -- it aliases the GL texture the engine draws into.
        if (_frameTex == null && Ran_Host_IsBooted() == 1)
        {
            uint tex = Ran_Host_GetFrameTexture();
            if (tex != 0)
            {
                _frameTex = Texture2D.CreateExternalTexture(
                    _texW, _texH, TextureFormat.RGBA32,
                    false, false, new IntPtr(tex));
                Debug.Log($"[RanHost] frame texture wrapped: id={tex}");
            }
        }
    }

    void OnGUI()
    {
        //	Deliberately crude: the ugly-boot milestone needs pixels on screen,
        //	not a UI.
        //
        //	NO V-flip. The first assumption ("GL renders the FBO bottom-up, so
        //	pre-flip the rect") put the whole world on its head on device --
        //	Unity already accounts for the GL texture convention when IMGUI
        //	samples an external texture, so compensating twice IS the flip.
        if (_frameTex != null)
        {
            GUI.DrawTexture(
                new Rect(0, 0, Screen.width, Screen.height),
                _frameTex, ScaleMode.StretchToFill, false);
        }
        else
        {
            string msg = !_dataPresent
                ? $"RAN: game data NOT FOUND at\n{_root}\nadb push the client, the app re-checks every ~2s"
                : Ran_Host_IsBooted() == 1 ? "RAN: waiting for frame texture..."
                                           : "RAN: booting (first frames load the world)...";
            GUI.Label(new Rect(20, 20, 1000, 120), msg);
        }
    }
#else
    void Awake()
    {
        Debug.LogWarning("[RanHost] native host runs on Android device builds only " +
                         "(desktop testing uses ranclient_desktop.exe).");
    }
#endif
}
