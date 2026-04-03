// BTDSys PeerCtrl – ReBuzz Managed Machine
// Original C++ © 2002-2008 Ed Powley (BTDSys)
// C# ReBuzz port
//
// Build:  dotnet build -c Release   (set ReBuzzDir in Directory.Build.props first)
// Deploy: DLL is written directly to $(ReBuzzDir)\Gear\Generators\

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl, ParameterDecl, Sample, WorkModes
using BuzzGUI.Interfaces;      // IMenuItem, IMachine, IParameter, IParameterGroup, IBuzz, etc.

namespace BTDSys.PeerCtrl
{
    // =========================================================================
    // Mapping envelope (piecewise-linear curve, identical to original)
    // =========================================================================

    [Serializable]
    public class EnvPoint
    {
        public int X { get; set; }   // 0-65535
        public int Y { get; set; }   // 0-65535
    }

    [Serializable]
    public class EnvData
    {
        // null by default: XmlSerializer replaces a null list cleanly.
        // If it were initialised to a non-null list, XmlSerializer would ADD
        // the deserialized items to it, causing duplicate/crossed points.
        public List<EnvPoint> Points { get; set; } = null;

        public static List<EnvPoint> DefaultPoints() => new List<EnvPoint>
        {
            new EnvPoint { X = 0,     Y = 0      },   // input 0%   → param 0%
            new EnvPoint { X = 65535, Y = 65535  }    // input 100% → param 100%
        };

        // Use ActivePoints everywhere instead of Points directly,
        // so null (not-yet-initialised) falls back to the default straight line.
        List<EnvPoint> ActivePoints => Points ?? DefaultPoints();

        public float Evaluate(float pos)
        {
            var pts = ActivePoints;
            if (pts.Count < 2) return pos;

            int p = 0;
            while (p < pts.Count - 2 && pts[p + 1].X <= pos) p++;

            var p0 = pts[p];
            var p1 = pts[p + 1];
            if (p1.X == p0.X) return p0.Y;

            float t = (pos - p0.X) / (float)(p1.X - p0.X);
            return p0.Y + t * (p1.Y - p0.Y);
        }

        public void Reset() => Points = DefaultPoints();
    }

    // =========================================================================
    // One assignment on a track  (a track may have many of these)
    // =========================================================================

    [Serializable]
    public class TrackAssignment : INotifyPropertyChanged
    {
        string _machineName    = "";
        int    _paramIndex     = -1;   // flat index into GetAllParams() list
        int    _targetTrack    = -1;   // -1 = all tracks on the target machine

        int    _midiChannel    = 0;    // 0=off, 1-16, 17=all
        int    _midiController = -1;   // -1=off, 0-127
        bool   _midiFeedback   = false;
        bool   _midiIncDec     = false;
        bool   _midiIncDecWrap = false;

        public string MachineName    { get => _machineName;    set { _machineName    = value ?? ""; N(); } }
        public int    ParamIndex     { get => _paramIndex;     set { _paramIndex     = value;        N(); } }
        public int    TargetTrack    { get => _targetTrack;    set { _targetTrack    = value;        N(); } }
        public int    MidiChannel    { get => _midiChannel;    set { _midiChannel    = value;        N(); } }
        public int    MidiController { get => _midiController; set { _midiController = value;        N(); } }
        public bool   MidiFeedback   { get => _midiFeedback;   set { _midiFeedback   = value;        N(); } }
        public bool   MidiIncDec     { get => _midiIncDec;     set { _midiIncDec     = value;        N(); } }
        public bool   MidiIncDecWrap { get => _midiIncDecWrap; set { _midiIncDecWrap = value;        N(); } }

        public EnvData Mapping { get; set; } = new EnvData();

        // Runtime – not serialised
        [System.Xml.Serialization.XmlIgnore]
        public IMachine   ResolvedMachine { get; set; }
        [System.Xml.Serialization.XmlIgnore]
        public IParameter ResolvedParam   { get; set; }

        [System.Xml.Serialization.XmlIgnore]
        public string Label =>
            ResolvedMachine == null
                ? $"[{_machineName}] (not found)"
                : ResolvedParam == null
                    ? $"{_machineName} / (no param)"
                    : _targetTrack < 0
                        ? $"{_machineName} / {ResolvedParam.Name}"
                        : $"{_machineName} / {ResolvedParam.Name} [tr {_targetTrack}]";

        /// <summary>
        /// Map value01 through the curve and set the target parameter.
        /// Mirrors CTrack::FloatToPVal + UpdateInertia send path from the original.
        /// </summary>
        public void ApplyValue(float value01, BuzzGUI.Interfaces.IBuzz buzz)
        {
            if (string.IsNullOrEmpty(_machineName) || _paramIndex < 0 || buzz == null) return;

            // Re-resolve fresh every call – IParameter/IMachine references become
            // stale after song reload when ReBuzz recreates these objects.
            var machine = buzz.Song?.Machines?.FirstOrDefault(m => m.Name == _machineName);
            if (machine == null) return;

            var allp = PeerCtrlMachine.GetAllParams(machine);
            if (_paramIndex >= allp.Count) return;
            var param = allp[_paramIndex];

            float mappedY = Mapping.Evaluate(value01 * 65535f);
            // mappedY is in [0,65535]: 0 = param min, 65535 = param max
            float f = mappedY / 65535f;

            int min  = param.MinValue;
            int max  = param.MaxValue + 1;
            int pval = (int)(f * (max - min) + min);
            pval = Math.Max(min, Math.Min(max - 1, pval));

            bool isTrackParam =
                param.Group != null &&
                param.Group.Type == ParameterGroupType.Track;

            if (_targetTrack < 0 && isTrackParam)
                for (int tr = 0; tr < machine.TrackCount; tr++)
                    param.SetValue(tr, pval);
            else
                param.SetValue(_targetTrack < 0 ? 0 : _targetTrack, pval);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void N() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    // =========================================================================
    // Runtime per-track state (not serialised – rebuilt from parameters each session)
    // =========================================================================

    public class TrackState
    {
        public float ValueCurrent  = 0f;
        public float ValueTarget   = 0f;
        public float ValueStep     = 0f;
        public bool  Sliding       = false;
        public float LastSent      = -1f;
        public bool  Slaved        = false;

        public List<TrackAssignment> Assignments = new List<TrackAssignment>();
    }

    // =========================================================================
    // Serialisable machine state  (assignments + configuration)
    // =========================================================================

    [Serializable]
    public class PeerCtrlState
    {
        /// <summary>One list per track (length == MAX_TRACKS).</summary>
        public List<List<TrackAssignment>> TrackAssignments { get; set; }
            = new List<List<TrackAssignment>>();

        // The "Attributes" from the original are stored here instead, because
        // the managed API has no equivalent of CMachineAttribute for properties
        // that aren't pattern-editor parameters.
        public int MidiIncDecAmount { get; set; } = 1024;
        public int SendFreq         { get; set; } = 2;
        public int StopOnMute       { get; set; } = 0;
    }

    // =========================================================================
    // The machine
    // =========================================================================

    [MachineDecl(
        Name        = "BTDSys PeerCtrl",
        ShortName   = "PeerCtrl",
        Author      = "Ed Powley / ReBuzz port",
        MaxTracks   = PeerCtrlMachine.MAX_TRACKS,
        InputCount  = 0,
        OutputCount = 0)]
    public class PeerCtrlMachine : IBuzzMachine, INotifyPropertyChanged
    {
        public const int MAX_TRACKS = 64;

        IBuzzMachineHost host;

        // ── Global parameter: Inertia ─────────────────────────────────────────
        [ParameterDecl(
            Name        = "Inertia",
            Description = "Glide time in ticks/10 (0 = off, 10 = 1 tick, 1280 = 128 ticks)",
            MinValue    = 0,
            MaxValue    = 1280,
            DefValue    = 0)]
        public int Inertia
        {
            get => _inertia;
            set
            {
                _inertia      = value;
                InertiaOn     = value > 0;
                InertiaLength = value / 10.0f;
                if (!InertiaOn)
                    for (int i = 0; i < MAX_TRACKS; i++)
                        StopInertia(_tracks[i]);
                N(nameof(Inertia));
            }
        }
        int   _inertia;
        public bool  InertiaOn     { get; private set; }
        public float InertiaLength { get; private set; }  // in ticks

        // ── Track parameter: Slaved ───────────────────────────────────────────
        // Method form: void MethodName(int value, int track)
        [ParameterDecl(
            Name        = "Slaved",
            Description = "Mirror the previous track's value (switch: 0=no, 1=yes)",
            MinValue    = 0,
            MaxValue    = 1,
            DefValue    = 0)]
        public void SetSlaved(int value, int track)
        {
            if ((uint)track >= MAX_TRACKS) return;
            // Track 0 can never be slaved (original behaviour)
            _tracks[track].Slaved = (track > 0) && (value == 1);
        }

        // ── Track parameter: Value ────────────────────────────────────────────
        [ParameterDecl(
            Name        = "Value",
            Description = "Control value (0 = 0%, 65534 = 100%) passed through the mapping curve",
            MinValue    = 0,
            MaxValue    = 65534,
            DefValue    = 32767)]
        public void SetValue(int value, int track)
        {
            if ((uint)track >= MAX_TRACKS) return;
            if (_tracks[track].Slaved) return;
            if (_initialising)
            {
                _initialising = false;
                ResolveAllMachines();
            }
            ValueChange(_tracks[track], track, value / 65534.0f);
        }

        // ── "Attribute-equivalent" configuration ─────────────────────────────
        // The original used Buzz CMachineAttribute for these.  In the managed
        // API there is no AttributeDecl equivalent, so they live in
        // PeerCtrlState and are edited through the Settings dialog.

        /// <summary>Step size for MIDI CC 96/97 inc/dec encoders (0-65534).</summary>
        public int MidiIncDecAmount { get; set; } = 1024;

        /// <summary>
        /// How many Work() calls between inertia pushes.
        /// 0 = tick only (no sub-tick updates).
        /// Mirrors the original "Ctrl Rate" / "Send Freq" attribute.
        /// </summary>
        public int SendFreq { get; set; } = 2;

        /// <summary>When 1, no control changes are sent while the machine is muted.</summary>
        public int StopOnMute { get; set; } = 0;

        // ── Internal runtime state ────────────────────────────────────────────
        readonly TrackState[] _tracks = new TrackState[MAX_TRACKS];
        int  _updateCounter;
        int  _samplesAccum;
        bool _initialising = true;

        IBuzz Buzz => host?.Machine?.Graph?.Buzz;

        // =====================================================================
        // Constructor
        // =====================================================================

        public PeerCtrlMachine(IBuzzMachineHost host)
        {
            this.host = host;
            for (int i = 0; i < MAX_TRACKS; i++)
                _tracks[i] = new TrackState();
        }

        // =====================================================================
        // IBuzzMachine – host binding
        // =====================================================================

        public IBuzzMachineHost Host
        {
            get => host;
            set => host = value;   // kept for interface compliance; set via constructor in practice
        }

        // =====================================================================
        // State serialisation
        // MachineState property name must match the class name used by ReBuzz
        // to know which type to (de)serialise.
        // =====================================================================

        public PeerCtrlState MachineState
        {
            get
            {
                var st = new PeerCtrlState
                {
                    MidiIncDecAmount = MidiIncDecAmount,
                    SendFreq         = SendFreq,
                    StopOnMute       = StopOnMute,
                };
                for (int t = 0; t < MAX_TRACKS; t++)
                    st.TrackAssignments.Add(
                        new List<TrackAssignment>(_tracks[t].Assignments));
                return st;
            }
            set
            {
                if (value == null) return;
                MidiIncDecAmount = value.MidiIncDecAmount;
                SendFreq         = value.SendFreq;
                StopOnMute       = value.StopOnMute;
                for (int t = 0; t < MAX_TRACKS; t++)
                {
                    _tracks[t].Assignments.Clear();
                    if (t < value.TrackAssignments.Count)
                        _tracks[t].Assignments.AddRange(value.TrackAssignments[t]);
                }
                // Mark init done – other machines may not be in graph yet so
                // resolution is retried in Tick(). SendNow no longer needs this guard.
                _initialising = false;
                ResolveAllMachines();
            }
        }

        // ReBuzz calls this after template import so machine renames are applied
        public void ImportFinished(IDictionary<string, string> nameMap)
        {
            foreach (var ts in _tracks)
                foreach (var a in ts.Assignments)
                    if (nameMap.TryGetValue(a.MachineName, out var n))
                        a.MachineName = n;
            ResolveAllMachines();
        }

        // =====================================================================
        // Machine-graph resolution
        // =====================================================================

        public void ResolveAssignment(TrackAssignment a)
        {
            a.ResolvedMachine = null;
            a.ResolvedParam   = null;
            if (Buzz == null || string.IsNullOrEmpty(a.MachineName)) return;

            a.ResolvedMachine = Buzz.Song.Machines
                .FirstOrDefault(m => m.Name == a.MachineName);

            if (a.ResolvedMachine == null || a.ParamIndex < 0) return;

            var allp = GetAllParams(a.ResolvedMachine);
            if (a.ParamIndex < allp.Count)
                a.ResolvedParam = allp[a.ParamIndex];
        }

        public void ResolveAllMachines()
        {
            if (Buzz == null) return;
            foreach (var ts in _tracks)
            {
                bool anyNewlyResolved = false;
                foreach (var a in ts.Assignments)
                {
                    bool wasMissing = a.ResolvedParam == null;
                    ResolveAssignment(a);
                    if (wasMissing && a.ResolvedParam != null)
                        anyNewlyResolved = true;
                }
                // Push the current value to any newly resolved targets immediately
                if (anyNewlyResolved && !_initialising)
                    SendNow(ts);
            }
        }

        /// <summary>
        /// Flat list of controllable parameters for a machine:
        /// global params first, then track params – skipping group 0 (internal).
        /// The list index is used as ParamIndex in TrackAssignment.
        /// </summary>
        public static List<IParameter> GetAllParams(IMachine m)
        {
            var list = new List<IParameter>();
            if (m?.ParameterGroups == null) return list;
            for (int g = 1; g < m.ParameterGroups.Count; g++)
            {
                var grp = m.ParameterGroups[g];
                if (grp?.Parameters != null)
                    list.AddRange(grp.Parameters);
            }
            return list;
        }

        // =====================================================================
        // Value-change + inertia engine
        // (mirrors CTrack::ValueChange + CTrack::UpdateInertia from original)
        // =====================================================================

        void ValueChange(TrackState ts, int trackIdx, float v01)
        {
            ts.ValueTarget = Clamp01(v01);

            if (InertiaOn && host != null)
            {
                int   spt   = host.MasterInfo?.SamplesPerTick ?? 256;
                float steps = InertiaLength * spt;
                if (steps > 0f)
                {
                    ts.ValueStep = (ts.ValueTarget - ts.ValueCurrent) / steps;
                    ts.Sliding   = true;
                }
                else
                {
                    ts.ValueCurrent = ts.ValueTarget;
                    ts.Sliding      = false;
                    SendNow(ts);
                }
            }
            else
            {
                // No inertia – apply immediately.
                // Work() may never be called on a no-audio control machine,
                // so we can't rely on UpdateInertia() to push the value out.
                ts.ValueCurrent = ts.ValueTarget;
                ts.Sliding      = false;
                SendNow(ts);
            }

            // Cascade: if the next track is Slaved, propagate the same value
            int next = trackIdx + 1;
            if (next < MAX_TRACKS && _tracks[next].Slaved)
                ValueChange(_tracks[next], next, v01);
        }

        void SendNow(TrackState ts)
        {
            ts.ValueCurrent = Clamp01(ts.ValueCurrent);
            var buzz = Buzz;
            foreach (var a in ts.Assignments)
                a.ApplyValue(ts.ValueCurrent, buzz);
            ts.LastSent = ts.ValueCurrent;
        }

        void StopInertia(TrackState ts)
        {
            ts.Sliding      = false;
            ts.ValueCurrent = ts.ValueTarget;
        }

        void UpdateInertia(TrackState ts, int numSamples)
        {
            bool shouldSend = false;

            if (ts.Sliding)
            {
                ts.ValueCurrent += ts.ValueStep * numSamples;

                bool overshot = (ts.ValueStep > 0f && ts.ValueCurrent >= ts.ValueTarget)
                             || (ts.ValueStep < 0f && ts.ValueCurrent <= ts.ValueTarget);
                if (overshot) StopInertia(ts);

                ts.ValueCurrent = Clamp01(ts.ValueCurrent);
                shouldSend      = true;
            }
            else if (ts.ValueCurrent != ts.LastSent)
            {
                shouldSend = true;
            }

            if (shouldSend && !_initialising)
            {
                var buzz = Buzz;
                foreach (var a in ts.Assignments)
                    a.ApplyValue(ts.ValueCurrent, buzz);
                ts.LastSent = ts.ValueCurrent;
            }
        }

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        // =====================================================================
        // IBuzzMachine – Tick / Work
        // =====================================================================

        public void Tick()
        {
            if (_initialising)
            {
                _initialising = false;
                ResolveAllMachines();
            }
            else
            {
                bool anyUnresolved = _tracks.Any(ts =>
                    ts.Assignments.Any(a => a.ResolvedParam == null && !string.IsNullOrEmpty(a.MachineName)));
                if (anyUnresolved)
                    ResolveAllMachines();
            }

            SyncFromParamValues();

            _samplesAccum  = 0;
            _updateCounter = 0;
        }

        void SyncFromParamValues()
        {
            try
            {
                var machine = host?.Machine;
                if (machine?.ParameterGroups == null) return;

                IParameterGroup trackGroup = null;
                foreach (var g in machine.ParameterGroups)
                    if (g?.Type == ParameterGroupType.Track) { trackGroup = g; break; }
                if (trackGroup == null) return;

                IParameter valueParam = null;
                foreach (var p in trackGroup.Parameters)
                    if (p.Name == "Value") { valueParam = p; break; }
                if (valueParam == null) return;

                int numTracks = machine.TrackCount;
                for (int t = 0; t < numTracks && t < MAX_TRACKS; t++)
                {
                    int raw = valueParam.GetValue(t);
                    if (raw == valueParam.NoValue) continue;
                    float v = Clamp01(raw / 65534.0f);
                    var ts = _tracks[t];
                    if (Math.Abs(v - ts.ValueCurrent) > 0.00001f || ts.LastSent < 0f)
                    {
                        ts.ValueCurrent = ts.ValueTarget = v;
                        SendNow(ts);
                    }
                }
            }
            catch { }
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            // Lazy resolution: Buzz graph may not be ready at constructor time
            if (_initialising)
            {
                ResolveAllMachines();
                _initialising = false;
            }

            _samplesAccum  += n;
            _updateCounter++;

            // Sub-tick inertia updates (mirrors original MDKWork behaviour)
            if (SendFreq != 0 && _updateCounter >= SendFreq)
            {
                int numTracks = host?.Machine?.TrackCount ?? 0;
                for (int t = 0; t < numTracks && t < MAX_TRACKS; t++)
                    UpdateInertia(_tracks[t], _samplesAccum);

                _samplesAccum  = 0;
                _updateCounter = 0;
            }

            return false;   // control machine – no audio output
        }

        // =====================================================================
        // MIDI CC input  (mirrors miex::MidiControlChange from original)
        // =====================================================================

        // Fired on every incoming MIDI CC – the settings dialog uses this for MIDI learn
        public event Action<int, int> MidiCCReceived;  // ctrl (0-127), channel (0-based)

        public void MidiControlChange(int ctrl, int channel, int value)
        {
            // Notify the settings dialog for MIDI learn (ctrl 96/97 excluded –
            // those are inc/dec meta-messages, not assignable controllers)
            if (ctrl != 96 && ctrl != 97)
                MidiCCReceived?.Invoke(ctrl, channel);

            int numTracks = host?.Machine?.TrackCount ?? 0;
            for (int t = 0; t < numTracks && t < MAX_TRACKS; t++)
            {
                var ts = _tracks[t];
                bool handled = false;
                foreach (var a in ts.Assignments)
                {
                    if (a.MidiChannel == 0)   continue;
                    if (a.MidiController < 0) continue;

                    bool chanOk = a.MidiChannel == 17
                               || a.MidiChannel == channel + 1;
                    if (!chanOk) continue;

                    if (a.MidiIncDec && (ctrl == 96 || ctrl == 97))
                    {
                        if (value != a.MidiController) continue;

                        float step = MidiIncDecAmount / 65534.0f;
                        float newT = ts.ValueTarget + (ctrl == 96 ? step : -step);

                        if (newT > 1.0f)
                            newT = a.MidiIncDecWrap ? newT - 1.0f : 1.0f;
                        else if (newT < 0.0f)
                            newT = a.MidiIncDecWrap ? newT + 1.0f : 0.0f;

                        ValueChange(ts, t, newT);
                        handled = true;
                    }
                    else if (ctrl == a.MidiController)
                    {
                        ValueChange(ts, t, value / 127.0f);
                        handled = true;
                    }
                }

                // Feed the new value back into the pattern editor's Value parameter
                // so the slider follows MIDI input visually
                if (handled)
                {
                    try
                    {
                        int pval = (int)(ts.ValueCurrent * 65534.0f + 0.5f);
                        pval = Math.Max(0, Math.Min(65534, pval));
                        // Group 2 = track params; Value is the last param in track group
                        var machine = host?.Machine;
                        if (machine?.ParameterGroups?.Count > 2)
                        {
                            var trackGroup = machine.ParameterGroups[2];
                            var valueParam = trackGroup?.Parameters?.LastOrDefault();
                            valueParam?.SetValue(t, pval);
                        }
                    }
                    catch { }
                }
            }
        }

        // =====================================================================
        // Context-menu commands
        // =====================================================================

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuEntry(0, "Assignment Settings...", OpenSettings),
            new MenuEntry(1, "About...",               ShowAbout),
        };

        /// <summary>
        /// Called by ReBuzz when the user clicks a context-menu item,
        /// matching the original C++ CMachineInterface::Command(int i) pattern.
        /// </summary>
        public void Command(int id)
        {
            switch (id)
            {
                case 0: OpenSettings(); break;
                case 1: ShowAbout();    break;
            }
        }

        SettingsWindow _settingsWindow;

        void OpenSettings()
        {
            // If already open, bring it to the front on its own thread
            if (_settingsWindow != null)
            {
                _settingsWindow.Dispatcher.Invoke(() => _settingsWindow.Activate());
                return;
            }

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    _settingsWindow = new SettingsWindow(this);
                    _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                    _settingsWindow.ShowDialog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"{ex.GetType().Name}\n{ex.Message}",
                        "BTDSys PeerCtrl",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    _settingsWindow = null;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        void ShowAbout()
        {
            MessageBox.Show(
                "BTDSys PeerCtrl 1.6\n" +
                "© 2002–2008 Ed Powley (BTDSys)\n\n" +
                "ReBuzz C# managed machine port.",
                "BTDSys PeerCtrl",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // =====================================================================
        // Helpers exposed to the settings dialog
        // =====================================================================

        public TrackState GetTrack(int i)    => _tracks[i];
        public int        ActiveTrackCount   => host?.Machine?.TrackCount ?? 1;
        public IBuzz      BuzzHost           => Buzz;

        // =====================================================================
        // INotifyPropertyChanged
        // =====================================================================

        public event PropertyChangedEventHandler PropertyChanged;
        void N(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // =========================================================================
    // MenuEntry – derives from DependencyObject so WPF TwoWay bindings on
    // IsChecked etc. work without TypeDescriptor/interface read-only issues.
    // =========================================================================

    sealed class MenuEntry : System.Windows.DependencyObject, IMenuItem,
                             System.ComponentModel.INotifyPropertyChanged
    {
        // --- DependencyProperties (WPF can always bind TwoWay to these) ---

        public static readonly System.Windows.DependencyProperty IsCheckedProperty =
            System.Windows.DependencyProperty.Register(
                "IsChecked", typeof(bool), typeof(MenuEntry),
                new System.Windows.PropertyMetadata(false));

        public static readonly System.Windows.DependencyProperty IsEnabledProperty =
            System.Windows.DependencyProperty.Register(
                "IsEnabled", typeof(bool), typeof(MenuEntry),
                new System.Windows.PropertyMetadata(true));

        // --- Constructor ---

        public MenuEntry(int id, string text, Action action)
        {
            ID      = id;
            Text    = text;
            _action = action;
        }
        readonly Action _action;

        // --- IMenuItem ---

        public int    ID           { get; }
        public string Text         { get; }
        public string GestureText  => null;
        public object CommandParameter => null;

        public bool IsChecked
        {
            get => (bool)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }
        public bool IsEnabled
        {
            get => (bool)GetValue(IsEnabledProperty);
            set => SetValue(IsEnabledProperty, value);
        }

        public bool IsCheckable      { get; set; } = false;
        public bool IsDefault        { get; set; } = false;
        public bool IsSeparator      { get; set; } = false;
        public bool IsLabel          { get; set; } = false;
        public bool StaysOpenOnClick { get; set; } = false;

        public IEnumerable<IMenuItem> Children => null;

        // ICommand – ReBuzz may call Execute; we also implement Command(int id)
        public System.Windows.Input.ICommand Command =>
            new RelayCommand(() => _action?.Invoke());

        // INotifyPropertyChanged
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged
        { add { } remove { } }
    }

    sealed class RelayCommand : System.Windows.Input.ICommand
    {
        readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter)    => _execute?.Invoke();
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
}
