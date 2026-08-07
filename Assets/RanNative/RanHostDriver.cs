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

    [Tooltip("Camera drag sensitivity, percent. 100 = raw pixels (too fast " +
             "per user test). 0 falls back to 40 (serialized-0 trap).")]
    public int LookSensitivityPercent = 40;

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
    int       _mouseId = -1;      // finger owning the mouse
    int       _dragId = -1;       // finger owning the camera drag (right half)
    int       _stickId = -1;      // finger owning the joystick (left half)
    Vector2   _stickAnchor;
    Vector2   _stickVec;
    Vector2   _dragStart;
    float     _dragTime;
    bool      _dragMoved;
    Vector2   _tapQueuedPos;      // queued right-side UI click, engine coords
    int       _tapQueuedFrames;   // 2 = held frame pending, 1 = release pending

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

    //	Scaled look with FLOAT accumulation: at 40% a 2px drag is 0.8px --
    //	truncating per-frame would eat slow drags entirely, so fractions
    //	carry over between frames.
    float _lookAccX, _lookAccY;
    void SendLook(float dx, float dy)
    {
        int pct = LookSensitivityPercent > 0 ? LookSensitivityPercent : 40;
        _lookAccX += dx * pct / 100f;
        _lookAccY += dy * pct / 100f;
        int ix = (int)_lookAccX, iy = (int)_lookAccY;
        if (ix != 0 || iy != 0) { Ran_Host_Look(ix, iy); _lookAccX -= ix; _lookAccY -= iy; }
    }

    //	Release the mouse WITHOUT parking it at (0,0): that corner sits on the
    //	HP bar, and hovering UI has engine side effects (cursor type, camera
    //	handling). Neutral = lower-centre of the frame, over open ground.
    void ReleaseMouse()
    {
        Ran_SetInput(_texW / 2, _texH * 2 / 3, 0, 0, 0);
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

        //	FINGER OWNERSHIP FIRST. The joystick finger stays the joystick and
        //	a second finger stays the camera drag -- "moving plus drag turned
        //	into zoom" (reported) because a raw touchCount>=2 check treated
        //	the stick+drag pair as a pinch. Pinch now requires BOTH fingers
        //	to be unowned.
        bool stickOwned = false, dragOwned = false;
        for (int i = 0; i < Input.touchCount; ++i)
        {
            int id = Input.GetTouch(i).fingerId;
            if (id == _stickId) stickOwned = true;
            if (id == _dragId)  dragOwned  = true;
        }

        //	TWO fingers: pinch = zoom, shared drag = look ("Shift view" on PC).
        //	The mouse is released so the fingers never read as clicks.
        if (Input.touchCount >= 2 && !stickOwned && !dragOwned)
        {
            Touch a = Input.GetTouch(0), b = Input.GetTouch(1);
            float   d = Vector2.Distance(a.position, b.position);
            Vector2 c = (a.position + b.position) * 0.5f;

            if (_prevPinch > 0f)
            {
                float dDist = d - _prevPinch;
                Vector2 cd  = c - _prevCentroid;

                //	ONE GESTURE AT A TIME. Sending both every frame made a
                //	rotate-drag trigger zoom (reported on device): two fingers
                //	never move perfectly parallel, so a drag always leaks a
                //	little distance change. Whichever signal dominates this
                //	frame wins; the other is ignored.
                if (Mathf.Abs(dDist) > cd.magnitude)
                {
                    int px = (int)dDist;
                    if (px != 0) Ran_Host_Zoom(px);
                }
                else if (cd.sqrMagnitude > 0.25f)
                {
                    //	Unity Y is up-positive; the engine's look wants
                    //	screen-down-positive dy.
                    SendLook(cd.x, -cd.y);
                }
            }
            _prevPinch = d;
            _prevCentroid = c;
            _dragId = -1;
            ReleaseMouse();
        }
        else if (Input.touchCount >= 1)
        {
            _prevPinch = -1f;

            //	PER-TOUCH, not per-count: joystick and camera drag must work
            //	SIMULTANEOUSLY (move while looking), so each finger is routed
            //	by ownership -- stick finger to the stick, drag finger (or a
            //	new right-half finger) to the camera.
            for (int i = 0; i < Input.touchCount; ++i)
            {
                Touch t = Input.GetTouch(i);
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;

                //	LEFT half = VIRTUAL JOYSTICK. Anchors where the finger
                //	lands; camera-relative walk; release issues a real stop.
                if (_stickId == t.fingerId ||
                    (_stickId < 0 && _dragId != t.fingerId &&
                     t.phase == TouchPhase.Began && t.position.x < Screen.width * 0.5f))
                {
                    if (ended)
                    {
                        if (_stickId >= 0) Ran_Host_MoveStop();
                        _stickId = -1; _stickVec = Vector2.zero;
                    }
                    else
                    {
                        if (_stickId < 0) { _stickId = t.fingerId; _stickAnchor = t.position; }
                        _stickVec = t.position - _stickAnchor;
                        if (_stickVec.magnitude > 20f)
                        {
                            Vector2 n = _stickVec.normalized;
                            Ran_Host_MoveDir(n.x, n.y);   // Unity y-up == forward
                        }
                        else Ran_Host_MoveStop();          // dead zone
                    }
                }
                //	RIGHT half: DRAG rotates the camera; a quick short tap is
                //	a UI click (menus and the tray live bottom-right).
                else if (_dragId == t.fingerId ||
                         (_dragId < 0 && t.phase == TouchPhase.Began))
                {
                    if (t.phase == TouchPhase.Began)
                    {
                        _dragId = t.fingerId; _dragStart = t.position;
                        _dragTime = Time.unscaledTime; _dragMoved = false;
                    }
                    Vector2 dp = t.deltaPosition;
                    if ((t.position - _dragStart).magnitude > 15f) _dragMoved = true;
                    if (_dragMoved && dp.sqrMagnitude > 0.25f)
                        SendLook(dp.x, -dp.y);

                    if (ended)
                    {
                        //	Short, still touch = a click. Queued: the engine's
                        //	edge machine needs a held frame then a release.
                        if (!_dragMoved && Time.unscaledTime - _dragTime < 0.3f)
                        {
                            Rect fit = FitRect();
                            _tapQueuedPos = new Vector2(
                                (t.position.x - fit.x) * _texW / fit.width,
                                (Screen.height - t.position.y - fit.y) * _texH / fit.height);
                            _tapQueuedFrames = 2;
                        }
                        _dragId = -1;
                    }
                }
            }
            if (_tapQueuedFrames <= 0) ReleaseMouse();
        }
        else
        {
            _prevPinch = -1f;
            _dragId = -1;
            if (_tapQueuedFrames <= 0) ReleaseMouse();
        }

        //	Deliver a queued right-side tap: one held frame, one release frame.
        if (_tapQueuedFrames > 0)
        {
            --_tapQueuedFrames;
            Ran_SetInput((int)_tapQueuedPos.x, (int)_tapQueuedPos.y,
                         _tapQueuedFrames > 0 ? 1 : 0, 0, 0);
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
