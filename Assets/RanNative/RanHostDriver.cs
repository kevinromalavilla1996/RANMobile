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

    [Tooltip("Engine render size. 0 = the full landscape screen resolution " +
             "(fills the display; RAN's UI anchors its panels to the frame " +
             "edges, so widescreen works like the PC widescreen clients). " +
             "Set e.g. 1024x768 to letterbox the classic 4:3 layout instead.")]
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

    //	Walk toward a tapped point (engine frame coords); runs the engine's own
    //	ActionMoveTo -- pathing, server message, walk animation included.
    [DllImport(LIB)] static extern void   Ran_Host_TapMove(float engineX, float engineY);

    //	Pinch zoom as mouse wheel (WHEEL_DELTA units, 120 per notch).
    [DllImport(LIB)] static extern void   Ran_Host_SetWheel(int delta);

    const int kEventFrame = 1;

    IntPtr    _renderEvent;
    Texture2D _frameTex;      // wraps the engine's FBO colour texture
    int       _texW, _texH;
    bool      _dataPresent;   // gate: booting without data null-derefs in CreatePC
    bool      _configured;    // Configure deferred until landscape is REAL
    string    _root;
    float     _prevPinch = -1f;   // two-finger distance last frame; <0 = not pinching
    float     _lastTapMove;       // hold-to-walk throttle (0.35s, desktop's value)

    void Awake()
    {
        //	Landscape MMO on a phone; also stops Unity re-creating the GL
        //	surface mid-run for rotations, which would orphan the engine's FBO.
        Screen.orientation = ScreenOrientation.LandscapeLeft;
        Application.targetFrameRate = 60;

        _root = string.IsNullOrEmpty(DataRootOverride)
            ? Path.Combine(Application.persistentDataPath, "RanData")
            : DataRootOverride;

        //	NO sizing here. Screen.width read in Awake races the orientation
        //	request above -- the first device build measured 1080x2392 and
        //	booted a portrait engine frame onto a horizontal phone. Configure
        //	runs from Update once the landscape dimensions are real.
    }

    void ConfigureOnce()
    {
        if (_configured) return;
        //	Explicit size: take it now. Screen size: wait for landscape.
        if (RenderWidth <= 0 || RenderHeight <= 0)
        {
            if (Screen.width <= Screen.height) return;   // rotation not applied yet
            _texW = Screen.width;
            _texH = Screen.height;
        }
        else
        {
            _texW = RenderWidth;
            _texH = RenderHeight;
        }

        Ran_Host_Configure(_root, _texW, _texH);
        _renderEvent = Ran_Host_GetRenderEventFunc();

        //	Case-insensitive on purpose: the pushed client may spell it data/
        //	or Data/, and this check must agree with the engine's resolver.
        RefreshDataPresent();
        Debug.Log($"[RanHost] data root: {_root}  engine {_texW}x{_texH}  " +
                  $"screen {Screen.width}x{Screen.height}  data present: {_dataPresent}");
        _configured = true;
    }

    //	The largest rect with the engine frame's aspect that fits the screen,
    //	centred. Shared by the blit and the touch mapping so they always agree.
    Rect FitRect()
    {
        float scale = Mathf.Min((float)Screen.width / _texW, (float)Screen.height / _texH);
        float w = _texW * scale, h = _texH * scale;
        return new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
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
        ConfigureOnce();
        if (!_configured) return;   // still waiting for landscape dimensions

        //	HARD GATE. Booting without the client data reaches CreatePC with
        //	0 maps and null-derefs (measured on device, 2026-08-07). Waiting
        //	here also lets an adb push finish while the app sits open.
        if (!_dataPresent)
        {
            if (Time.frameCount % 120 == 0) RefreshDataPresent();
            return;
        }

        Ran_Host_SetDelta(Time.unscaledDeltaTime);

        //	TWO fingers: pinch zoom, delivered as wheel delta. The mouse is
        //	released during a pinch so the second finger landing does not
        //	read as a click.
        if (Input.touchCount >= 2)
        {
            float d = Vector2.Distance(Input.GetTouch(0).position,
                                       Input.GetTouch(1).position);
            if (_prevPinch > 0f)
                Ran_Host_SetWheel((int)((d - _prevPinch) * 2f));
            _prevPinch = d;
            Ran_SetInput(0, 0, 0, 0, 0);
        }
        //	ONE finger = the mouse, mapped through the SAME letterbox rect the
        //	blit uses -- a touch must land on the engine pixel it appears over.
        //	Holding the finger also WALKS toward it, reissued on the same
        //	throttle the desktop walk keys use (ActionMoveTo restarts the walk;
        //	per-frame reissue freezes the animation on its first pose).
        else if (Input.touchCount == 1)
        {
            _prevPinch = -1f;
            Ran_Host_SetWheel(0);

            Touch t = Input.GetTouch(0);
            Rect fit = FitRect();
            int mx = (int)((t.position.x - fit.x) * _texW / fit.width);
            //	Unity touch origin is bottom-left; the engine is top-left.
            int my = (int)((Screen.height - t.position.y - fit.y) * _texH / fit.height);
            bool held = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
            Ran_SetInput(mx, my, held ? 1 : 0, 0, 0);

            if (held && Time.unscaledTime - _lastTapMove > 0.35f)
            {
                Ran_Host_TapMove(mx, my);
                _lastTapMove = Time.unscaledTime;
            }
        }
        else
        {
            _prevPinch = -1f;
            Ran_Host_SetWheel(0);
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
        //
        //	LETTERBOXED, not stretched: the engine renders the 4:3 frame this
        //	UI was authored for; pillarbox bars on a 20:9 panel beat a UI
        //	whose buttons are twice as wide as they are tall.
        if (_frameTex != null)
        {
            GUI.DrawTexture(FitRect(), _frameTex, ScaleMode.StretchToFill, false);
        }
        else
        {
            string msg = !_configured
                ? "RAN: waiting for landscape orientation..."
                : !_dataPresent
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
