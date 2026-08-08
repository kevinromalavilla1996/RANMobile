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
             "renders at (the blit upscales to fill). 100 = native. The upload " +
             "war made 100 affordable (render was 6ms at 75). 0 falls back to " +
             "100 -- scene components keep their serialized 0 from before this " +
             "field existed.")]
    public int RenderScalePercent = 100;

    [Tooltip("Camera drag sensitivity, percent. 100 = raw pixels (too fast " +
             "per user test). 0 falls back to 40 (serialized-0 trap).")]
    public int LookSensitivityPercent = 40;

    [Header("Character Setup (gameemulator.exe's startup dialog)")]
    [Tooltip("A .charset preset from Data/GLogic (class00..class4A). Empty = " +
             "class00.charset, the emulator's default.")]
    public string CharsetFile = "";
    [Tooltip("Character name. Empty = the host default.")]
    public string CharName = "";
    [Tooltip("0 = normal, 1 = GM, 2 = admin. Matches the dialog's radios. " +
             "NOTE: admin item tooltips show extra debug lines by design.")]
    public int UserLevel = 0;
    public bool MaxLevel;
    public bool MaxStats;
    public bool AllSkills;
    public bool Gold1B;
    public bool Contrib100K;
    public bool Activity100K;

    //	World units ahead of the player a joystick/tap move targets. Native
    //	clamps to [30, 500]; below ~30 the character reaches the target before
    //	the next reissue and stutter-stops. Live-tunable in the editor.
    [Header("Movement")]
    [Range(30, 500)] public int MoveAimUnits = 80;
    int _appliedMoveAim = -1;

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
    [DllImport(LIB)] static extern void   Ran_Host_SetMoveAim(int units);

    //	Two-finger camera look, drag pixel deltas.
    [DllImport(LIB)] static extern void   Ran_Host_Look(int dx, int dy);

    //	Keyboard bridge: a tap is one engine-frame press of a DirectInput scan
    //	code -- the whole desktop key surface reduces to buttons calling this
    //	(see _Port_ARM64/docs/mobile_input_map.md). KeyHold is for modifiers.
    [DllImport(LIB)] static extern void   Ran_Host_KeyTap(int dik);
    [DllImport(LIB)] static extern void   Ran_Host_KeyHold(int dik, int down);

    //	The emulator startup dialog as an export; must run BEFORE the first
    //	engine frame boots (ConfigureOnce calls it ahead of Configure).
    [DllImport(LIB)] static extern void   Ran_Host_SetCharSetup(
        string charsetFile, string charName, int userLevel, int bMaxLevel,
        int bMaxStats, int bAllSkills, int b1BGold, int b100KContrib,
        int b100KActivity);

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

    //	On-screen key bars (skill 1-0, items QWEASD). Rects live in GUI space
    //	(top-left origin); touches are converted at the hit test. A finger that
    //	BEGINS on a button is owned by it -- it must never leak into the
    //	joystick/camera/tap routing (same ownership rule as stick and drag).
    int       _barId = -1;         // finger owning a button press
    int       _barPressed = -1;    // which button that finger went down on
    bool      _uiDragMode;         // long-press: finger IS the held mouse (drag & drop)

    //	Release-position hold. The engine consumes the mailbox one frame
    //	behind; parking right after a release let the park OVERWRITE the
    //	up-position before the engine read it -- the UP edge then landed at
    //	the park, so drops (skill -> quickbar slot) missed their target
    //	("L UP at 1196,720" in every drag log = the park, not the finger).
    //	After any release, the cursor stays put, button up, for a few frames.
    int       _releaseHoldFrames;
    Vector2   _releaseHoldPos;     // engine coords

    //	RIGHT-CLICK = a quick two-finger tap (both fingers down and up fast,
    //	no pinch/rotate movement), delivered at the two fingers' centre.
    //	Desktop right-click casts the active skill on a target and clears a
    //	quickbar slot; both now exist on glass.
    float     _twoStart = -1f;     // when the two-finger state began; <0 = not live
    bool      _twoMoved;           // any zoom/look applied = not a tap
    Vector2   _twoCentroid;
    int       _tapQueuedBtn;       // 0 = left, 1 = right (shared delivery queue)
    Rect[]    _barRects;
    int[]     _barDiks;
    string[]  _barLabels;

    static readonly int[] kSkillDiks = { 0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B }; // DIK_1..0
    static readonly int[] kItemDiks  = { 0x10,0x11,0x12,0x1E,0x1F,0x20 };                      // Q W E A S D

    //	The 17 window shortcuts (RANPARAM MenuShotcut, decode in
    //	docs/mobile_input_map.md) behind a fold-out toggle. Order mirrors the
    //	SHOTCUT_ enum: INVEN CHAR SKILL PARTY QUEST CLUB FRIEND MAP CHATMACRO
    //	ITEMBANK ITEMSHOP RUN HELP PET ATTACKMODE PKMODE SUMMON.
    static readonly int[] kMenuDiks = {
        0x17 /*I*/, 0x2E /*C*/, 0x25 /*K*/, 0x19 /*P*/, 0x14 /*T*/, 0x22 /*G*/,
        0x21 /*F*/, 0x32 /*M*/, 0x30 /*B*/, 0x13 /*R*/, 0x23 /*H*/, 0x26 /*L*/,
        0x2D /*X*/, 0x2C /*Z*/, 0x16 /*U*/, 0x24 /*J*/, 0x18 /*O*/ };
    static readonly string[] kMenuLabels = {
        "INV","CHR","SKL","PTY","QST","CLB","FRD","MAP","MCR","BNK","SHP",
        "RUN","HLP","PET","ATK","PK","SUM" };

    const int kMenuToggle = -2;    // BarHit's answer for the KEYS button
    bool      _menuOpen;
    Rect      _menuToggleRect;
    Rect[]    _menuRects;

    void BuildBars()
    {
        //	Two rows, bottom-centre, floating just above the game's own tray.
        //	Sized off screen HEIGHT so phones of any aspect get thumbable
        //	buttons; translucent so the world stays visible under them.
        float s   = Screen.height * 0.072f;
        float gap = s * 0.12f;
        int   n   = kSkillDiks.Length + kItemDiks.Length;
        _barRects  = new Rect[n];
        _barDiks   = new int[n];
        _barLabels = new string[n];

        string[] skillLbl = { "1","2","3","4","5","6","7","8","9","0" };
        string[] itemLbl  = { "Q","W","E","A","S","D" };

        float rowW = kSkillDiks.Length * s + (kSkillDiks.Length - 1) * gap;
        float x0   = (Screen.width - rowW) * 0.5f;
        float ySkill = Screen.height - s * 2.6f;    // above the engine's tray
        for (int i = 0; i < kSkillDiks.Length; ++i)
        {
            _barRects[i]  = new Rect(x0 + i * (s + gap), ySkill, s, s);
            _barDiks[i]   = kSkillDiks[i];
            _barLabels[i] = skillLbl[i];
        }

        float rowW2 = kItemDiks.Length * s + (kItemDiks.Length - 1) * gap;
        float x1    = (Screen.width - rowW2) * 0.5f;
        float yItem = ySkill - s - gap;
        for (int i = 0; i < kItemDiks.Length; ++i)
        {
            int j = kSkillDiks.Length + i;
            _barRects[j]  = new Rect(x1 + i * (s + gap), yItem, s, s);
            _barDiks[j]   = kItemDiks[i];
            _barLabels[j] = itemLbl[i];
        }

        //	KEYS toggle: top-right corner, out of the compass's way. The panel
        //	folds out beneath it, two columns.
        float s2 = Screen.height * 0.058f;
        _menuToggleRect = new Rect(Screen.width - s2 * 2.4f, Screen.height * 0.28f,
                                   s2 * 2.1f, s2);
        _menuRects = new Rect[kMenuDiks.Length];
        for (int i = 0; i < kMenuDiks.Length; ++i)
        {
            int   col = i % 2, row = i / 2;
            float mx  = Screen.width - s2 * 2.4f + col * (s2 * 1.08f);
            float my  = _menuToggleRect.y + s2 * 1.2f + row * (s2 * 1.08f);
            _menuRects[i] = new Rect(mx, my, s2, s2);
        }
    }

    int BarHit(Vector2 touchPos)
    {
        if (_barRects == null) return -1;
        Vector2 gui = new Vector2(touchPos.x, Screen.height - touchPos.y);
        for (int i = 0; i < _barRects.Length; ++i)
            if (_barRects[i].Contains(gui)) return i;
        if (_menuToggleRect.Contains(gui)) return kMenuToggle;
        if (_menuOpen && _menuRects != null)
            for (int i = 0; i < _menuRects.Length; ++i)
                if (_menuRects[i].Contains(gui)) return _barRects.Length + i;
        return -1;
    }

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
            //	Fallback 100, not 75: the scene's component serialized 0 before
            //	this field existed, so THIS line -- not the script default --
            //	decides the real resolution.
            int pct = RenderScalePercent > 0 ? RenderScalePercent : 100;
            _texW = Mathf.Max(640, Screen.width  * pct / 100);
            _texH = Mathf.Max(360, Screen.height * pct / 100);
        }
        else
        {
            _texW = RenderWidth;
            _texH = RenderHeight;
        }

        //	Character setup BEFORE the boot consumes it. Empty strings keep the
        //	emulator defaults; the inspector is the phone's CDlgCharset.
        if (!string.IsNullOrEmpty(CharsetFile) || !string.IsNullOrEmpty(CharName) ||
            UserLevel != 0 || MaxLevel || MaxStats || AllSkills || Gold1B ||
            Contrib100K || Activity100K)
        {
            Ran_Host_SetCharSetup(CharsetFile ?? "", CharName ?? "", UserLevel,
                                  MaxLevel ? 1 : 0, MaxStats ? 1 : 0,
                                  AllSkills ? 1 : 0, Gold1B ? 1 : 0,
                                  Contrib100K ? 1 : 0, Activity100K ? 1 : 0);
            Debug.Log($"[RanHost] char setup: '{CharsetFile}' '{CharName}' " +
                      $"lvl={UserLevel} max={MaxLevel}/{MaxStats} skills={AllSkills} gold={Gold1B}");
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

    //	Runs when the two-finger state ends: a short, still two-finger contact
    //	is a RIGHT-CLICK at the centre point, queued through the shared tap
    //	delivery (held frame, release frame, position hold).
    void DetectTwoFingerTap()
    {
        if (_twoStart < 0f) return;
        bool quick = Time.unscaledTime - _twoStart < 0.35f;
        if (quick && !_twoMoved)
        {
            Rect fit = FitRect();
            _tapQueuedPos = new Vector2(
                (_twoCentroid.x - fit.x) * _texW / fit.width,
                (Screen.height - _twoCentroid.y - fit.y) * _texH / fit.height);
            _tapQueuedFrames = 4;	// hover, down, held, release
            _tapQueuedBtn = 1;
        }
        _twoStart = -1f;
    }

    //	Park -- unless a release just happened, in which case the cursor sits
    //	at the release point (button up) until the engine has surely seen it.
    void ParkOrHold()
    {
        if (_releaseHoldFrames > 0)
        {
            --_releaseHoldFrames;
            Ran_SetInput((int)_releaseHoldPos.x, (int)_releaseHoldPos.y, 0, 0, 0);
        }
        else ReleaseMouse();
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
        if (_barRects == null) BuildBars();   // landscape is real past this point

        if (MoveAimUnits != _appliedMoveAim)
        {
            Ran_Host_SetMoveAim(MoveAimUnits);
            _appliedMoveAim = MoveAimUnits;
        }

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
        //	BUTTONS CLAIM FIRST. A finger that lands on a bar button belongs to
        //	it for its whole life; the tap fires on release while still inside
        //	the same button (slide off to cancel -- standard button feel).
        for (int i = 0; i < Input.touchCount; ++i)
        {
            Touch t = Input.GetTouch(i);
            if (_barId < 0 && t.phase == TouchPhase.Began)
            {
                //	-1 = no button. Everything ELSE is a button, including the
                //	KEYS toggle's kMenuToggle sentinel (-2) -- `hit >= 0` here
                //	is exactly the bug that made the toggle untappable.
                int hit = BarHit(t.position);
                if (hit != -1) { _barId = t.fingerId; _barPressed = hit; }
            }
            else if (_barId == t.fingerId &&
                     (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled))
            {
                if (t.phase == TouchPhase.Ended && BarHit(t.position) == _barPressed)
                {
                    if (_barPressed == kMenuToggle)
                        _menuOpen = !_menuOpen;
                    else if (_barPressed >= _barRects.Length)
                        Ran_Host_KeyTap(kMenuDiks[_barPressed - _barRects.Length]);
                    else
                        Ran_Host_KeyTap(_barDiks[_barPressed]);
                }
                _barId = -1; _barPressed = -1;
            }
        }

        bool stickOwned = false, dragOwned = false, barOwned = false;
        for (int i = 0; i < Input.touchCount; ++i)
        {
            int id = Input.GetTouch(i).fingerId;
            if (id == _stickId) stickOwned = true;
            if (id == _dragId)  dragOwned  = true;
            if (id == _barId)   barOwned   = true;
        }

        //	TWO fingers: pinch = zoom, shared drag = look ("Shift view" on PC).
        //	The mouse is released so the fingers never read as clicks.
        if (Input.touchCount >= 2 && !stickOwned && !dragOwned && !barOwned)
        {
            Touch a = Input.GetTouch(0), b = Input.GetTouch(1);
            float   d = Vector2.Distance(a.position, b.position);
            Vector2 c = (a.position + b.position) * 0.5f;

            if (_prevPinch < 0f) { _twoStart = Time.unscaledTime; _twoMoved = false; }
            _twoCentroid = c;

            if (_prevPinch > 0f)
            {
                float dDist = d - _prevPinch;
                Vector2 cd  = c - _prevCentroid;

                //	ONE GESTURE AT A TIME. Sending both every frame made a
                //	rotate-drag trigger zoom (reported on device): two fingers
                //	never move perfectly parallel, so a drag always leaks a
                //	little distance change. Whichever signal dominates this
                //	frame wins; the other is ignored.
                if (Mathf.Abs(dDist) > 3f || cd.magnitude > 3f) _twoMoved = true;

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
            DetectTwoFingerTap();
            _prevPinch = -1f;

            //	PER-TOUCH, not per-count: joystick and camera drag must work
            //	SIMULTANEOUSLY (move while looking), so each finger is routed
            //	by ownership -- stick finger to the stick, drag finger (or a
            //	new right-half finger) to the camera.
            for (int i = 0; i < Input.touchCount; ++i)
            {
                Touch t = Input.GetTouch(i);
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;

                //	A button's finger is the button's alone.
                if (_barId == t.fingerId) continue;

                //	BOTTOM-LEFT ZONE = VIRTUAL JOYSTICK (was the whole left
                //	half -- which made every window on the left side of the
                //	screen untouchable: no clicks, no drags. The stick keeps
                //	its anchor-where-the-thumb-lands feel inside its corner;
                //	everywhere else gets the full click/long-press-drag/rotate
                //	gestures, so windows can be used and dragged ANYWHERE).
                if (_stickId == t.fingerId ||
                    (_stickId < 0 && _dragId != t.fingerId &&
                     t.phase == TouchPhase.Began &&
                     t.position.x < Screen.width * 0.42f &&
                     //	The game's own vertical quickbar hugs the left EDGE;
                     //	the stick must not claim it or skills/items can never
                     //	be clicked or dropped into the lower slots (reported:
                     //	"doesn't let me put skill on the slot").
                     t.position.x > Screen.height * 0.06f &&
                     t.position.y < Screen.height * 0.60f))
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
                //	RIGHT half, three outcomes by what the finger does FIRST:
                //	  moves quickly            -> camera rotate (as before)
                //	  short still tap          -> UI click (as before)
                //	  stays put >= 0.35s       -> HELD MOUSE: the finger drags
                //	    with the left button down until release. This is what
                //	    makes drag & drop work -- items to quick slots, skills
                //	    to the bar, windows by their title bars ("can't drag
                //	    item to quickslot", reported on device).
                else if (_dragId == t.fingerId ||
                         (_dragId < 0 && t.phase == TouchPhase.Began))
                {
                    if (t.phase == TouchPhase.Began)
                    {
                        _dragId = t.fingerId; _dragStart = t.position;
                        _dragTime = Time.unscaledTime; _dragMoved = false;
                        _uiDragMode = false;
                    }

                    if (_uiDragMode)
                    {
                        //	The finger IS the mouse, button held, every frame.
                        Rect fit = FitRect();
                        int ex = (int)((t.position.x - fit.x) * _texW / fit.width);
                        int ey = (int)((Screen.height - t.position.y - fit.y) * _texH / fit.height);
                        Ran_SetInput(ex, ey, ended ? 0 : 1, 0, 0);
                        if (ended)
                        {
                            _uiDragMode = false; _dragId = -1;
                            _releaseHoldFrames = 4;
                            _releaseHoldPos = new Vector2(ex, ey);
                        }
                        continue;
                    }

                    //	HOVER AT THE FINGER while the touch is down (button up).
                    //	The engine's window-drag latches its grab offset against
                    //	the LAST mouse position it saw -- and the per-frame park
                    //	used to win that race, latching gap=(-922,358) instead
                    //	of (102,10) (probe-measured): the window teleported off
                    //	the right edge and the boundary clamp pinned it there.
                    {
                        Rect fitH = FitRect();
                        Ran_SetInput(
                            (int)((t.position.x - fitH.x) * _texW / fitH.width),
                            (int)((Screen.height - t.position.y - fitH.y) * _texH / fitH.height),
                            0, 0, 0);
                    }

                    Vector2 dp = t.deltaPosition;
                    if ((t.position - _dragStart).magnitude > 15f) _dragMoved = true;
                    if (_dragMoved && dp.sqrMagnitude > 0.25f)
                        SendLook(dp.x, -dp.y);

                    //	Still and held long enough: promote to held-mouse mode.
                    if (!_dragMoved && !ended &&
                        Time.unscaledTime - _dragTime >= 0.35f)
                    {
                        _uiDragMode = true;
                        continue;   // next frame starts pressing at the finger
                    }

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
                            _tapQueuedFrames = 4;	// hover, down, held, release
                        }
                        _dragId = -1;
                    }
                }
            }
            //	NEVER park while a right-half finger is down: the park was
            //	overwriting the hover every frame (last write wins), which is
            //	exactly how the drag latched its offset against the park.
            if (_tapQueuedFrames <= 0 && !_uiDragMode && _dragId < 0) ParkOrHold();
        }
        else
        {
            DetectTwoFingerTap();
            _prevPinch = -1f;
            _dragId = -1;
            _uiDragMode = false;
            if (_tapQueuedFrames <= 0) ParkOrHold();
        }

        //	Deliver a queued tap (left OR right button). Four frames:
        //	HOVER first, then down, held, release. The hover frame is what
        //	makes taps TARGET mobs: the engine's entity-under-cursor tracking
        //	runs a frame behind the cursor, so a click landing in the same
        //	frame as the teleport was judged against the PARK position (open
        //	ground) and became a walk instead of a target-click. Same law as
        //	the window-drag gap latch: the mouse must ARRIVE before it acts.
        if (_tapQueuedFrames > 0)
        {
            --_tapQueuedFrames;
            int held = (_tapQueuedFrames > 0 && _tapQueuedFrames < 3) ? 1 : 0;
            Ran_SetInput((int)_tapQueuedPos.x, (int)_tapQueuedPos.y,
                         _tapQueuedBtn == 0 ? held : 0,
                         _tapQueuedBtn == 1 ? held : 0, 0);
            if (_tapQueuedFrames == 0)
            {
                _tapQueuedBtn = 0;   // queue defaults back to left clicks
                //	Click-carry (tap skill, tap slot) needs the release to be
                //	SEEN at the tap position too.
                _releaseHoldFrames = 4;
                _releaseHoldPos = _tapQueuedPos;
            }
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

            //	The key bars. Translucent boxes; the pressed one goes opaque.
            //	IMGUI is not consulted for input -- Update's touch routing owns
            //	that (finger ownership) -- these are pixels only.
            if (_barRects != null)
            {
                GUIStyle style = GUI.skin.box;
                int savedSize = style.fontSize;
                FontStyle savedStyle = style.fontStyle;
                style.fontSize  = (int)(_barRects[0].height * 0.42f);
                style.fontStyle = FontStyle.Bold;
                Color saved = GUI.color;
                for (int i = 0; i < _barRects.Length; ++i)
                {
                    GUI.color = i == _barPressed
                        ? new Color(1f, 1f, 0.6f, 0.95f)
                        : new Color(1f, 1f, 1f, 0.45f);
                    GUI.Box(_barRects[i], _barLabels[i], style);
                }

                //	Window-shortcut panel behind the KEYS toggle.
                int menuFont = (int)(_menuToggleRect.height * 0.38f);
                style.fontSize = menuFont;
                GUI.color = _barPressed == kMenuToggle
                    ? new Color(1f, 1f, 0.6f, 0.95f)
                    : new Color(1f, 1f, 1f, 0.55f);
                GUI.Box(_menuToggleRect, _menuOpen ? "KEYS ▲" : "KEYS ▼", style);
                if (_menuOpen)
                {
                    for (int i = 0; i < _menuRects.Length; ++i)
                    {
                        GUI.color = _barPressed == _barRects.Length + i
                            ? new Color(1f, 1f, 0.6f, 0.95f)
                            : new Color(1f, 1f, 1f, 0.5f);
                        GUI.Box(_menuRects[i], kMenuLabels[i], style);
                    }
                }
                GUI.color = saved;
                style.fontSize  = savedSize;
                style.fontStyle = savedStyle;
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
