#Requires -Version 7.0
<#
.SYNOPSIS
    Reports the controller backend the port will actually use, and proves the
    polling path works.

.DESCRIPTION
    The port reads pads through RecompOne's InputManager, which drives SDL2's
    game-controller API. On Windows that API reaches an Xbox pad through SDL's
    XInput backend, so "does this build have XInput support" is really three
    separate questions, and this script answers all three rather than asserting
    any of them:

      1. Is the XInput backend compiled into the SDL2.dll that ships beside the
         port? Checked by scanning the DLL itself, because a build without
         SDL_XINPUT_ENABLED fails at runtime, not at build time.
      2. Does SDL still enumerate the pad under the exact hint sequence
         InputManager.Initialize uses - specifically SDL_JOYSTICK_RAWINPUT=0,
         which disables the RawInput backend and is what pushes XInput-capable
         devices onto the XInput driver? The device path SDL reports names the
         driver that won: 'XInput#0' means the XInput backend took the pad.
      3. Does button and axis state actually come back? Read from a real pad when
         one is attached, and otherwise from an SDL virtual controller that this
         script attaches, so the polling path is exercised with nothing plugged
         in.

    The virtual-pad check is the one that makes this usable as a regression gate:
    it needs no hardware, and it fails if SDL's polling path or the
    game-controller mapping ever breaks.

    ORDERING IS LOAD-BEARING. The virtual-pad check must run before any physical
    pad has been opened and polled. Once a real controller has been updated in
    this process, a virtual controller attached afterwards reports no state at
    all - SDL_JoystickSetVirtualButton succeeds but reads stay zero. That was
    reproduced directly (enumerate-only, hint set, real-pad-open/close all pass;
    adding a poll of the real pad fails). It is an SDL virtual-device quirk
    rather than a defect in the port, which never attaches virtual pads, but it
    silently turns this self-test into a false failure if the sections are
    reordered.

.PARAMETER SdlPath
    SDL2.dll to probe. Defaults to the newest one found under the port's build
    output, then dist/, then the RecompOne checkout.

.PARAMETER RawInput
    Value for the SDL_JOYSTICK_RAWINPUT hint. The runtime sets 0. Pass 1 to
    compare which driver takes the device.

.PARAMETER VirtualPad
    Attach an SDL virtual game controller and verify a button and an axis round
    trip through it. Needs no hardware.

.PARAMETER RequireController
    Exit non-zero when no physical game controller is attached.

.EXAMPLE
    pwsh -File tools/Test-GamepadBackend.ps1 -VirtualPad

.EXAMPLE
    pwsh -File tools/Test-GamepadBackend.ps1 -RawInput 1 -RequireController
#>
[CmdletBinding()]
param(
    [string]$SdlPath,
    [ValidateSet(0, 1)]
    [int]$RawInput = 0,
    [switch]$VirtualPad,
    [switch]$RequireController
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

function Resolve-SdlPath {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "SDL2.dll not found: $Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $candidates = @(
        Join-Path $root 'port/RE15pc/bin/Release/net10.0/runtimes/win-x64/native/SDL2.dll'
        Join-Path $root 'dist/SDL2.dll'
        Join-Path $root 'RecompOne/RecompOne.Runtime/bin/Release/net10.0/runtimes/win-x64/native/SDL2.dll'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }

    $found = Get-ChildItem -Path $root -Recurse -Filter 'SDL2.dll' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($found) { return $found.FullName }

    throw 'no SDL2.dll found. Build the port first (tools/Publish-Exe.ps1 or dotnet build).'
}

$sdl = Resolve-SdlPath -Explicit $SdlPath
Write-Host "SDL2.dll : $sdl"
Write-Host ''

# ---------------------------------------------------------------------------
# 1. Static check: is the XInput backend in this build?
# ---------------------------------------------------------------------------
# SDL registers its drivers by name and carries a built-in mapping for XInput
# devices, so these markers are present only when the backend was compiled in.
$buildMarkers = [ordered]@{
    'SDL_XINPUT_ENABLED' = 'XInput driver compiled in'
    'xinput,*,a:b0'      = 'built-in XInput game-controller mapping'
    'XInputGetState'     = 'XInput state API resolved at runtime'
    'XInputSetState'     = 'XInput rumble API resolved at runtime'
}

Write-Host '--- 1. XInput backend compiled into SDL2.dll ---'
$ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($sdl))
$xinputCompiled = $true
foreach ($marker in $buildMarkers.Keys) {
    $present = $ascii.Contains($marker)
    if ($marker -eq 'SDL_XINPUT_ENABLED' -and -not $present) { $xinputCompiled = $false }
    Write-Host ("  {0,-26} {1}  ({2})" -f $marker, $(if ($present) { 'yes' } else { 'NO' }), $buildMarkers[$marker])
}
$hidapiCompiled = $ascii.Contains('SDL_JOYSTICK_HIDAPI')
Write-Host ("  {0,-26} {1}  ({2})" -f 'SDL_JOYSTICK_HIDAPI', $(if ($hidapiCompiled) { 'yes' } else { 'NO' }), 'Bluetooth / non-XInput pads')

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class Re15Sdl
{
    public const int JOYSTICK_TYPE_GAMECONTROLLER = 1;
    public const int BUTTON_A = 0;      // south face button: Xbox A, PS Cross
    public const int AXIS_LEFTX = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SdlVersion { public byte Major, Minor, Patch; }

    [StructLayout(LayoutKind.Sequential)]
    public struct JoystickGuid { public uint A, B, C, D; }

    // SDL_Event is a 56-byte union tagged by its first field.
    [StructLayout(LayoutKind.Sequential, Size = 56)]
    public struct SdlEvent { public uint Type; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_GetVersion(out SdlVersion v);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_SetHint(string name, string value);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GetHint(string name);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_InitSubSystem(uint flags);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern uint SDL_WasInit(uint flags);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GetError();

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_NumJoysticks();
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_IsGameController(int index);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GameControllerNameForIndex(int index);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_JoystickNameForIndex(int index);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern JoystickGuid SDL_JoystickGetDeviceGUID(int index);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void SDL_JoystickGetGUIDString(JoystickGuid guid, System.Text.StringBuilder text, int size);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_JoystickPathForIndex(int index);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GameControllerOpen(int index);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_GameControllerClose(IntPtr gc);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_GameControllerGetAttached(IntPtr gc);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GameControllerGetJoystick(IntPtr gc);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_GameControllerGetButton(IntPtr gc, int button);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern short SDL_GameControllerGetAxis(IntPtr gc, int axis);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_GameControllerUpdate();

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_JoystickAttachVirtual(int type, int naxes, int nbuttons, int nhats);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_JoystickDetachVirtual(int index);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_JoystickSetVirtualButton(IntPtr js, int button, byte value);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_JoystickSetVirtualAxis(IntPtr js, int axis, short value);
    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_JoystickUpdate();

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_PollEvent(out SdlEvent ev);

    public static string Text(IntPtr p) { return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p); }
    public static string Error() { return Text(SDL_GetError()); }
}
'@

$null = [Re15Sdl]::LoadLibraryW($sdl)
$version = New-Object Re15Sdl+SdlVersion
[Re15Sdl]::SDL_GetVersion([ref]$version)
Write-Host ''
Write-Host ("SDL version: {0}.{1}.{2}" -f $version.Major, $version.Minor, $version.Patch)

# ---------------------------------------------------------------------------
# 2. The runtime's own initialisation sequence, then enumerate. Enumeration
#    only: nothing is opened or polled yet, so the section below stays valid.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '--- 2. enumeration under the runtime hint sequence ---'

# Exactly what InputManager.Initialize does, in the same order.
$null = [Re15Sdl]::SDL_SetHint('SDL_JOYSTICK_RAWINPUT', [string]$RawInput)
$initResult = [Re15Sdl]::SDL_InitSubSystem(0x2000)
Write-Host ("  SDL_JOYSTICK_RAWINPUT  = {0}" -f [Re15Sdl]::Text([Re15Sdl]::SDL_GetHint('SDL_JOYSTICK_RAWINPUT')))
Write-Host ("  SDL_InitSubSystem(GC)  = {0}{1}" -f $initResult, $(if ($initResult -eq 0) { '' } else { "  error: $([Re15Sdl]::Error())" }))
foreach ($pair in @(@('EVENTS', 0x4000), @('JOYSTICK', 0x200), @('GAMECONTROLLER', 0x2000))) {
    Write-Host ("  subsystem {0,-15} = {1}" -f $pair[0], $(if ([Re15Sdl]::SDL_WasInit([uint32]$pair[1]) -ne 0) { 'initialised' } else { 'not initialised' }))
}

$count = [Re15Sdl]::SDL_NumJoysticks()
Write-Host ("  devices seen           = {0}" -f $count)

$controllers = @()
$driverByIndex = @{}
for ($i = 0; $i -lt $count; $i++) {
    $isPad = [Re15Sdl]::SDL_IsGameController($i) -eq 1
    $guid = [Re15Sdl]::SDL_JoystickGetDeviceGUID($i)
    $sb = [System.Text.StringBuilder]::new(64)
    [Re15Sdl]::SDL_JoystickGetGUIDString($guid, $sb, 64)
    $path = '(unavailable)'
    try { $path = [Re15Sdl]::Text([Re15Sdl]::SDL_JoystickPathForIndex($i)) } catch { }
    $name = [Re15Sdl]::Text([Re15Sdl]::SDL_GameControllerNameForIndex($i))
    if (-not $name) { $name = [Re15Sdl]::Text([Re15Sdl]::SDL_JoystickNameForIndex($i)) }

    # SDL reports the winning driver as a path prefix such as 'XInput#0'.
    $driver = if ($path -match '^([A-Za-z]+)#') { $Matches[1] } else { 'unknown' }
    $driverByIndex[$i] = $driver

    if ($isPad) {
        $controllers += [pscustomobject]@{ Index = $i; Name = $name; Guid = $sb.ToString(); Path = $path; Driver = $driver }
    }
    Write-Host ("   [{0}] gameController={1,-5} driver={2,-8} name='{3}' guid={4}" -f $i, $isPad, $driver, $name, $sb.ToString())
    Write-Host ("        path='{0}'" -f $path)
}

if ($controllers.Count -eq 0) { Write-Host '  no game controller attached.' }

# ---------------------------------------------------------------------------
# 3. Virtual pad: exercise the polling path with no hardware.
#    Must run before section 4 - see the ordering note in the header.
# ---------------------------------------------------------------------------
$virtualOk = $null
if ($VirtualPad) {
    Write-Host ''
    Write-Host '--- 3. polling path, verified against an SDL virtual controller ---'
    $virtualIndex = [Re15Sdl]::SDL_JoystickAttachVirtual([Re15Sdl]::JOYSTICK_TYPE_GAMECONTROLLER, 6, 15, 0)
    if ($virtualIndex -lt 0) {
        Write-Host ("  SDL_JoystickAttachVirtual failed: {0}" -f [Re15Sdl]::Error())
        $virtualOk = $false
    }
    else {
        $virtualController = [Re15Sdl]::SDL_GameControllerOpen($virtualIndex)
        $joystick = [Re15Sdl]::SDL_GameControllerGetJoystick($virtualController)
        try {
            Write-Host ("  attached virtual controller at index {0} (name '{1}')" -f `
                $virtualIndex, [Re15Sdl]::Text([Re15Sdl]::SDL_GameControllerNameForIndex($virtualIndex)))

            # The setters return a status. Ignoring it turns "the virtual device
            # was never driven" into "the polling path is broken", which are very
            # different problems.
            $setButton = [Re15Sdl]::SDL_JoystickSetVirtualButton($joystick, [Re15Sdl]::BUTTON_A, 1)
            $setAxis = [Re15Sdl]::SDL_JoystickSetVirtualAxis($joystick, [Re15Sdl]::AXIS_LEFTX, 20000)
            if ($setButton -ne 0 -or $setAxis -ne 0) {
                Write-Host ("  SetVirtualButton={0} SetVirtualAxis={1} error: {2}" -f `
                    $setButton, $setAxis, [Re15Sdl]::Error())
            }

            # PollEvent pumps the joystick state exactly as InputManager's
            # per-frame PollGamepadEvents does.
            $ev = New-Object Re15Sdl+SdlEvent
            $null = [Re15Sdl]::SDL_PollEvent([ref]$ev)
            $button = [Re15Sdl]::SDL_GameControllerGetButton($virtualController, [Re15Sdl]::BUTTON_A)
            $axis = [Re15Sdl]::SDL_GameControllerGetAxis($virtualController, [Re15Sdl]::AXIS_LEFTX)
            Write-Host ("  press  -> button A={0} leftX={1}" -f $button, $axis)

            $null = [Re15Sdl]::SDL_JoystickSetVirtualButton($joystick, [Re15Sdl]::BUTTON_A, 0)
            $null = [Re15Sdl]::SDL_JoystickSetVirtualAxis($joystick, [Re15Sdl]::AXIS_LEFTX, 0)
            $null = [Re15Sdl]::SDL_JoystickUpdate()
            $null = [Re15Sdl]::SDL_PollEvent([ref]$ev)
            $buttonUp = [Re15Sdl]::SDL_GameControllerGetButton($virtualController, [Re15Sdl]::BUTTON_A)
            $axisUp = [Re15Sdl]::SDL_GameControllerGetAxis($virtualController, [Re15Sdl]::AXIS_LEFTX)
            Write-Host ("  release-> button A={0} leftX={1}" -f $buttonUp, $axisUp)

            $virtualOk = ($button -eq 1) -and ($buttonUp -eq 0) -and ($axis -ne 0) -and ($axisUp -eq 0)
            Write-Host ("  verdict: {0}" -f $(if ($virtualOk) { 'button and axis round trip both PASS' } else { 'FAIL - polling path did not deliver state' }))
        }
        finally {
            if ($virtualController -ne [IntPtr]::Zero) { [Re15Sdl]::SDL_GameControllerClose($virtualController) }
            $null = [Re15Sdl]::SDL_JoystickDetachVirtual($virtualIndex)
        }
    }
}

# ---------------------------------------------------------------------------
# 4. Live state from the physical pad. Runs last: polling a real pad perturbs
#    SDL's virtual-device handling, which would break section 3.
# ---------------------------------------------------------------------------
if ($controllers.Count -gt 0) {
    Write-Host ''
    Write-Host '  live state of the first attached pad (press something to see it move):'
    $pad = [Re15Sdl]::SDL_GameControllerOpen($controllers[0].Index)
    if ($pad -eq [IntPtr]::Zero) {
        Write-Host ("    SDL_GameControllerOpen failed: {0}" -f [Re15Sdl]::Error())
    }
    else {
        try {
            for ($sample = 1; $sample -le 3; $sample++) {
                $ev = New-Object Re15Sdl+SdlEvent
                $null = [Re15Sdl]::SDL_PollEvent([ref]$ev)
                [Re15Sdl]::SDL_GameControllerUpdate()
                $pressed = @()
                for ($b = 0; $b -lt 21; $b++) {
                    if ([Re15Sdl]::SDL_GameControllerGetButton($pad, $b) -ne 0) { $pressed += $b }
                }
                Write-Host ("    sample {0}: attached={1} leftX={2,6} leftY={3,6} rightX={4,6} rightY={5,6} buttons=[{6}]" -f `
                    $sample, [Re15Sdl]::SDL_GameControllerGetAttached($pad),
                    [Re15Sdl]::SDL_GameControllerGetAxis($pad, 0), [Re15Sdl]::SDL_GameControllerGetAxis($pad, 1),
                    [Re15Sdl]::SDL_GameControllerGetAxis($pad, 2), [Re15Sdl]::SDL_GameControllerGetAxis($pad, 3),
                    ($pressed -join ','))
                if ($sample -lt 3) { Start-Sleep -Milliseconds 150 }
            }
        }
        finally { [Re15Sdl]::SDL_GameControllerClose($pad) }
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== summary ==='
$problems = @()
if (-not $xinputCompiled) { $problems += 'SDL2.dll has no XInput backend compiled in' }
if ($virtualOk -eq $false) { $problems += 'the game-controller polling path failed its virtual-pad round trip' }
if ($initResult -ne 0) { $problems += "SDL_InitSubSystem(GAMECONTROLLER) failed: $([Re15Sdl]::Error())" }
if ($RequireController -and $controllers.Count -eq 0) { $problems += 'no physical game controller is attached' }

Write-Host ("  XInput backend   : {0}" -f $(if ($xinputCompiled) { 'present' } else { 'MISSING' }))
Write-Host ("  RawInput backend : {0} (runtime sets SDL_JOYSTICK_RAWINPUT=0)" -f $(if ($RawInput -eq 0) { 'disabled' } else { 'enabled' }))
Write-Host ("  HIDAPI backend   : {0}" -f $(if ($hidapiCompiled) { 'present' } else { 'absent' }))
Write-Host ("  game controllers : {0}" -f $controllers.Count)
if ($controllers.Count -gt 0) {
    Write-Host ("  drivers in use   : {0}" -f (($controllers | ForEach-Object { "$($_.Name) -> $($_.Driver)" }) -join '; '))
}
if ($VirtualPad) {
    Write-Host ("  polling self-test: {0}" -f $(if ($virtualOk) { 'PASS' } else { 'FAIL' }))
}

if ($problems.Count -gt 0) {
    Write-Host ''
    foreach ($problem in $problems) { Write-Host "  FAIL: $problem" }
    exit 1
}

Write-Host ''
Write-Host "  PASS: pads are read through SDL's game-controller API with the XInput backend available."
exit 0
