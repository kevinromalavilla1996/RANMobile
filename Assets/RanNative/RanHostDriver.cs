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

    [Tooltip("Engine render size. 0 = screen resolution scaled by " +
             "RenderScalePercent. Set e.g. 1024x768 to letterbox 4:3 instead.")]
    public int RenderWidth = 0;
    public int RenderHeight = 0;

    [Tooltip("When RenderWidth is 0: percent of screen resolution the engine " +
             "renders at (the blit upscales to fill). 75 cuts pixel cost ~45% " +
             "for mild softening. 0 falls back to 75 -- components already " +
             "placed in a scene keep serialized 0 when this field is added.")]
    public int RenderScalePercent = 75;

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

    //	Pinch zoom, raw pixels (positive = fingers apart); the native side
    //	drives the camera's zoom directly.
    [DllImport(LIB)] static extern void   Ran_Host_Zoom(int pixels);

    //	Virtual joystick: normalized direction, y up = away from camera.
    //	Native runs the desktop-verified camera-relative walk with its own
    //	reissue rule.
    [DllImport(LIB)] static extern void   Ran_Host_MoveDir(float dirX, float dirY);
    [DllImport(LIB)] static extern void   Ran_Host_MoveStop();

    //	Two-finger camera look, drag pixel deltas.
    [DllImport(LIB)] static extern void   Ran_Host_Look(int dx, int dy);

    const int kEventFrame = 1;

    IntPtr    _renderEvent;
    Texture2D _frameTex;      // wraps the engine's FBO colour texture
    int       _texW, _texH;
    bool      _dataPresent;   // gate: booting without data null-derefs in CreatePC
    bool      _configured;    // Configure deferred until landscape is REAL
    string    _root;
    float     _prevPinch = -1f;   // two-finger distance last frame; <0 = not pinching
    Vector2   _prevCentroid;      // two-finger centroid last frame (for look)
    int       _stickId = -1;      // fingerId anchoring the joystick; -1 = none
    Vector2   _stickAnchor;       // screen position where the stick finger landed
    Vector2   _stickVec;          // current stick vector (for the on-screen hint)

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
            int pct = RenderScalePercent > 0 ? RenderScalePercent : 75;
            _texW = Mathf.Max(640, Screen.width  * pct / 100);
            _texH = Mathf.Max(360, Screen.height * pct / 100);
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

    void ReleaseStick()
    {
        if (_stickId >= 0) Ran_Host_MoveStop();
        _stickId = -1;
        _stickVec = Vector2.zero;
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

        //	TWO fingers: pinch = zoom, shared drag = look ("Shift view" on PC).
        //	The mouse is released so the fingers never read as clicks.
        if (Input.touchCount >= 2)
        {
            Touch a = Input.GetTouch(0), b = Input.GetTouch(1);
            float   d = Vector2.Distance(a.position, b.position);
            Vector2 c = (a.position + b.position) * 0.5f;

            if (_prevPinch > 0f)
            {
                int px = (int)(d - _prevPinch);
                if (px != 0) Ran_Host_Zoom(px);

                //	Centroid drag rotates the camera. Unity Y is up-positive;
                //	the engine's look expects screen-down-positive dy.
                Vector2 cd = c - _prevCentroid;
                if (cd.sqrMagnitude > 0.25f)
                    Ran_Host_Look((int)cd.x, (int)-cd.y);
            }
            _prevPinch = d;
            _prevCentroid = c;
            ReleaseStick();
            Ran_SetInput(0, 0, 0, 0, 0);
        }
        else if (Input.touchCount == 1)
        {
            _prevPinch = -1f;
            Touch t = Input.GetTouch(0);

            //	LEFT 40% of the screen = VIRTUAL JOYSTICK. Anchors where the
            //	finger lands; direction+hold walks, camera-relative, via the
            //	desktop-verified native path. Never sends mouse events.
            bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
            if (_stickId == t.fingerId || (_stickId < 0 && !ended &&
                t.phase == TouchPhase.Began && t.position.x < Screen.width * 0.4f))
            {
                if (ended) { ReleaseStick(); }
                else
                {
                    if (_stickId < 0) { _stickId = t.fingerId; _stickAnchor = t.position; }
                    _stickVec = t.position - _stickAnchor;
                    if (_stickVec.magnitude > 20f)
                    {
                        Vector2 n = _stickVec.normalized;
                        Ran_Host_MoveDir(n.x, n.y);   // Unity y-up == forward
                    }
                    else Ran_Host_MoveStop();         // inside dead zone
                }
                Ran_SetInput(0, 0, 0, 0, 0);
            }
            //	RIGHT side = the mouse: UI clicks and the engine's own
            //	click-to-move, mapped through the blit's letterbox rect.
            else
            {
                Rect fit = FitRect();
                int mx = (int)((t.position.x - fit.x) * _texW / fit.width);
                //	TOP-LEFT origin: the ground pick (GetMouseTargetPosWnd:1149)
                //	flips Y itself, so it expects top-left input.
                int my = (int)((Screen.height - t.position.y - fit.y) * _texH / fit.height);
                bool held = !ended;
                Ran_SetInput(mx, my, held ? 1 : 0, 0, 0);
            }
        }
        else
        {
            _prevPinch = -1f;
            ReleaseStick();
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

            //	Joystick hint: anchor ring + current stick position. IMGUI
            //	boxes are crude but zero-asset; replaced with real UI later.
            if (_stickId >= 0)
            {
                Vector2 a = new Vector2(_stickAnchor.x, Screen.height - _stickAnchor.y);
                Vector2 p = a + new Vector2(_stickVec.x, -_stickVec.y);
                GUI.Box(new Rect(a.x - 60, a.y - 60, 120, 120), "");
                GUI.Box(new Rect(p.x - 25, p.y - 25, 50, 50), "");
            }
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
