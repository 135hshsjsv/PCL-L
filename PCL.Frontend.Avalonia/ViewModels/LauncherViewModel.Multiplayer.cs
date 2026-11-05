using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using PCL.Core.App.Essentials;
using PCL.Frontend.Avalonia.Workflows;

namespace PCL.Frontend.Avalonia.ViewModels;

internal sealed record MultiplayerMemberViewModel(string Name, string KindLabel, bool IsLocal);

internal sealed partial class LauncherViewModel
{
    private static readonly Regex MultiplayerRoomCodePattern = new(
        @"^U/[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly TimeSpan MultiplayerActivePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MultiplayerIdlePollInterval = TimeSpan.FromSeconds(5);

    private FrontendTerracottaService? _terracottaService;
    private ActionCommand? _startMultiplayerHostingCommand;
    private ActionCommand? _joinMultiplayerRoomCommand;
    private ActionCommand? _leaveMultiplayerRoomCommand;
    private ActionCommand? _retryMultiplayerCommand;
    private CancellationTokenSource? _multiplayerCts;
    private long _multiplayerStateIndex = -1;
    private bool _isMultiplayerPageActive;
    private bool _isMultiplayerBusy;
    private bool _isMultiplayerSessionActive;
    private double _multiplayerProgress;
    private string _multiplayerCurrentState = "idle";
    private string _multiplayerStatusText = string.Empty;
    private string _multiplayerStatusDetail = string.Empty;
    private string? _multiplayerErrorText;
    private string _multiplayerJoinCode = string.Empty;
    private string? _multiplayerRoomCode;
    private string? _multiplayerDirectAddress;
    private IReadOnlyList<MultiplayerMemberViewModel> _multiplayerMembers = [];

    public ActionCommand StartMultiplayerHostingCommand =>
        _startMultiplayerHostingCommand ??= new ActionCommand(() => _ = StartMultiplayerHostingAsync());

    public ActionCommand JoinMultiplayerRoomCommand =>
        _joinMultiplayerRoomCommand ??= new ActionCommand(() => _ = JoinMultiplayerRoomAsync());

    public ActionCommand LeaveMultiplayerRoomCommand =>
        _leaveMultiplayerRoomCommand ??= new ActionCommand(() => _ = LeaveMultiplayerRoomAsync());

    public ActionCommand RetryMultiplayerCommand =>
        _retryMultiplayerCommand ??= new ActionCommand(() => _ = EnsureMultiplayerReadyAsync(forceRestart: true));

    public bool IsMultiplayerPageActive
    {
        get => _isMultiplayerPageActive;
        private set => SetProperty(ref _isMultiplayerPageActive, value);
    }

    public bool IsMultiplayerBusy
    {
        get => _isMultiplayerBusy;
        private set
        {
            if (SetProperty(ref _isMultiplayerBusy, value))
            {
                NotifyMultiplayerAvailabilityChanged();
            }
        }
    }

    public double MultiplayerProgress
    {
        get => _multiplayerProgress;
        private set => SetProperty(ref _multiplayerProgress, value);
    }

    public string MultiplayerStatusText
    {
        get => _multiplayerStatusText;
        private set => SetProperty(ref _multiplayerStatusText, value);
    }

    public string MultiplayerStatusDetail
    {
        get => _multiplayerStatusDetail;
        private set
        {
            if (SetProperty(ref _multiplayerStatusDetail, value))
            {
                RaisePropertyChanged(nameof(ShowMultiplayerStatusDetail));
            }
        }
    }

    public bool ShowMultiplayerStatusDetail => !string.IsNullOrEmpty(_multiplayerStatusDetail);

    public string? MultiplayerErrorText
    {
        get => _multiplayerErrorText;
        private set
        {
            if (SetProperty(ref _multiplayerErrorText, value))
            {
                RaisePropertyChanged(nameof(ShowMultiplayerError));
            }
        }
    }

    public bool ShowMultiplayerError => !string.IsNullOrEmpty(_multiplayerErrorText);

    public string MultiplayerJoinCode
    {
        get => _multiplayerJoinCode;
        set
        {
            if (SetProperty(ref _multiplayerJoinCode, value ?? string.Empty))
            {
                NotifyMultiplayerAvailabilityChanged();
            }
        }
    }

    public string? MultiplayerRoomCode
    {
        get => _multiplayerRoomCode;
        private set
        {
            if (SetProperty(ref _multiplayerRoomCode, value))
            {
                RaisePropertyChanged(nameof(ShowMultiplayerRoomCode));
            }
        }
    }

    public bool ShowMultiplayerRoomCode => !string.IsNullOrEmpty(_multiplayerRoomCode);

    public string? MultiplayerDirectAddress
    {
        get => _multiplayerDirectAddress;
        private set
        {
            if (SetProperty(ref _multiplayerDirectAddress, value))
            {
                RaisePropertyChanged(nameof(ShowMultiplayerDirectAddress));
            }
        }
    }

    public bool ShowMultiplayerDirectAddress => !string.IsNullOrEmpty(_multiplayerDirectAddress);

    public IReadOnlyList<MultiplayerMemberViewModel> MultiplayerMembers
    {
        get => _multiplayerMembers;
        private set
        {
            if (SetProperty(ref _multiplayerMembers, value))
            {
                RaisePropertyChanged(nameof(ShowMultiplayerMembers));
            }
        }
    }

    public bool ShowMultiplayerMembers => _multiplayerMembers.Count > 0;

    public bool CanStartMultiplayerHosting => !_isMultiplayerBusy && !_isMultiplayerSessionActive;

    public bool CanJoinMultiplayerRoom =>
        !_isMultiplayerBusy && !_isMultiplayerSessionActive && MultiplayerRoomCodePattern.IsMatch(_multiplayerJoinCode.Trim());

    public bool CanLeaveMultiplayerRoom => _isMultiplayerBusy || _isMultiplayerSessionActive;

    public bool ShowMultiplayerJoinInput => !_isMultiplayerSessionActive;

    public string MultiplayerTitleText => LT("shell.multiplayer.title");

    public string MultiplayerCopyrightText => LT("shell.multiplayer.copyright");

    public string MultiplayerJoinCodeWatermark => LT("shell.multiplayer.join.placeholder");

    public string MultiplayerHostButtonText => LT("shell.multiplayer.action.host");

    public string MultiplayerJoinButtonText => LT("shell.multiplayer.action.join");

    public string MultiplayerLeaveButtonText => LT("shell.multiplayer.action.leave");

    public string MultiplayerRoomCodeLabelText => LT("shell.multiplayer.room_code.label");

    public string MultiplayerDirectAddressLabelText => LT("shell.multiplayer.direct_address.label");

    public string MultiplayerMembersLabelText => LT("shell.multiplayer.members.label");

    public string MultiplayerJoinCodeLabelText => LT("shell.multiplayer.join.label");

    internal void RefreshMultiplayerLocalization()
    {
        RaisePropertyChanged(nameof(MultiplayerTitleText));
        RaisePropertyChanged(nameof(MultiplayerCopyrightText));
        RaisePropertyChanged(nameof(MultiplayerJoinCodeWatermark));
        RaisePropertyChanged(nameof(MultiplayerHostButtonText));
        RaisePropertyChanged(nameof(MultiplayerJoinButtonText));
        RaisePropertyChanged(nameof(MultiplayerLeaveButtonText));
        RaisePropertyChanged(nameof(MultiplayerRoomCodeLabelText));
        RaisePropertyChanged(nameof(MultiplayerDirectAddressLabelText));
        RaisePropertyChanged(nameof(MultiplayerMembersLabelText));
        RaisePropertyChanged(nameof(MultiplayerJoinCodeLabelText));

        if (_multiplayerCurrentState != "idle")
        {
            MultiplayerStatusText = BuildMultiplayerStatusText(_multiplayerCurrentState, MultiplayerDirectAddress);
        }
    }

    internal void ShutdownMultiplayer()
    {
        var previous = Interlocked.Exchange(ref _multiplayerCts, null);
        previous?.Cancel();
        previous?.Dispose();

        var service = _terracottaService;
        _terracottaService = null;
        service?.Dispose();
    }

    internal void UpdateMultiplayerPageActivation(bool isActive)
    {
        if (IsMultiplayerPageActive == isActive)
        {
            return;
        }

        IsMultiplayerPageActive = isActive;
        if (isActive)
        {
            RefreshMultiplayerLocalization();
            _ = EnsureMultiplayerReadyAsync(forceRestart: false);
        }
    }

    private FrontendTerracottaService EnsureTerracottaService()
    {
        return _terracottaService ??= new FrontendTerracottaService(_launcherActionService.RuntimePaths);
    }

    private async Task EnsureMultiplayerReadyAsync(bool forceRestart)
    {
        var cancellationToken = ResetMultiplayerCancellation().Token;
        var service = EnsureTerracottaService();

        if (service.IsRunning && !forceRestart)
        {
            StartMultiplayerPolling(cancellationToken);
            return;
        }

        IsMultiplayerBusy = true;
        MultiplayerErrorText = null;
        MultiplayerProgress = 0d;

        try
        {
            if (!service.IsInstalled)
            {
                MultiplayerStatusText = LT("shell.multiplayer.status.installing");
                var progress = new Progress<double>(value =>
                {
                    MultiplayerProgress = value;
                    MultiplayerStatusText = LT(
                        "shell.multiplayer.status.installing_progress",
                        ("percent", (int)Math.Round(value * 100)));
                });
                await service.EnsureInstalledAsync(progress, cancellationToken);
            }

            MultiplayerStatusText = LT("shell.multiplayer.status.starting");
            await service.StartAsync(cancellationToken);

            // Never discard a room that is already running: only normalise a genuinely idle
            // instance back to "waiting". Re-entering the page used to reset live rooms here.
            if (!_isMultiplayerSessionActive)
            {
                var current = await service.GetStateAsync(cancellationToken);
                if (string.Equals(current.State, "waiting", StringComparison.OrdinalIgnoreCase))
                {
                    await service.ResetToIdleAsync(cancellationToken);
                }
            }

            _multiplayerStateIndex = -1;
            ApplyMultiplayerState(FrontendTerracottaState.Empty);
            StartMultiplayerPolling(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The user closed the launcher or restarted the session.
        }
        catch (Exception ex)
        {
            ShowMultiplayerFailure(ex);
        }
        finally
        {
            IsMultiplayerBusy = false;
        }
    }

    private async Task StartMultiplayerHostingAsync()
    {
        if (!CanStartMultiplayerHosting)
        {
            return;
        }

        var cancellationToken = ResetMultiplayerCancellation().Token;
        IsMultiplayerBusy = true;
        MultiplayerErrorText = null;

        try
        {
            var service = EnsureTerracottaService();
            if (!service.IsRunning)
            {
                await service.StartAsync(cancellationToken);
            }

            if (!await service.StartHostingAsync(LaunchUserName, cancellationToken))
            {
                MultiplayerErrorText = LT("shell.multiplayer.error.host_unavailable");
                return;
            }

            _isMultiplayerSessionActive = true;
            ApplyMultiplayerState(new FrontendTerracottaState(0, "host-scanning", null, null, [], null, null, 0));
            StartMultiplayerPolling(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ignored: superseded by a newer command.
        }
        catch (Exception ex)
        {
            ShowMultiplayerFailure(ex);
        }
        finally
        {
            IsMultiplayerBusy = false;
            NotifyMultiplayerAvailabilityChanged();
        }
    }

    private async Task JoinMultiplayerRoomAsync()
    {
        var roomCode = (MultiplayerJoinCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!MultiplayerRoomCodePattern.IsMatch(roomCode))
        {
            MultiplayerErrorText = LT("shell.multiplayer.error.invalid_code");
            return;
        }

        var cancellationToken = ResetMultiplayerCancellation().Token;
        IsMultiplayerBusy = true;
        MultiplayerErrorText = null;

        try
        {
            var service = EnsureTerracottaService();
            if (!service.IsRunning)
            {
                await service.StartAsync(cancellationToken);
            }

            if (!await service.JoinRoomAsync(roomCode, LaunchUserName, cancellationToken))
            {
                MultiplayerErrorText = LT("shell.multiplayer.error.room_not_found");
                return;
            }

            _isMultiplayerSessionActive = true;
            ApplyMultiplayerState(new FrontendTerracottaState(0, "guest-connecting", roomCode, null, [], null, null, 0));
            StartMultiplayerPolling(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ignored: superseded by a newer command.
        }
        catch (Exception ex)
        {
            ShowMultiplayerFailure(ex);
        }
        finally
        {
            IsMultiplayerBusy = false;
            NotifyMultiplayerAvailabilityChanged();
        }
    }

    private async Task LeaveMultiplayerRoomAsync()
    {
        var cancellationToken = ResetMultiplayerCancellation().Token;
        IsMultiplayerBusy = true;

        try
        {
            var service = _terracottaService;
            if (service is not null && (service.IsRunning || service.IsAttached))
            {
                await service.ResetToIdleAsync(cancellationToken);
            }

            _isMultiplayerSessionActive = false;
            _multiplayerStateIndex = -1;
            MultiplayerErrorText = null;
            ApplyMultiplayerState(FrontendTerracottaState.Empty);
            StartMultiplayerPolling(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ignored: superseded by a newer command.
        }
        catch (Exception ex)
        {
            ShowMultiplayerFailure(ex);
        }
        finally
        {
            IsMultiplayerBusy = false;
            NotifyMultiplayerAvailabilityChanged();
        }
    }

    private CancellationTokenSource ResetMultiplayerCancellation()
    {
        var previous = Interlocked.Exchange(ref _multiplayerCts, null);
        previous?.Cancel();
        previous?.Dispose();

        var cancellationTokenSource = new CancellationTokenSource();
        _multiplayerCts = cancellationTokenSource;
        return cancellationTokenSource;
    }

    private void StartMultiplayerPolling(CancellationToken cancellationToken)
    {
        _ = PollMultiplayerStateAsync(cancellationToken);
    }

    private async Task PollMultiplayerStateAsync(CancellationToken cancellationToken)
    {
        var service = _terracottaService;
        if (service is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = await service.GetStateAsync(cancellationToken);
                if (state.Index >= _multiplayerStateIndex)
                {
                    _multiplayerStateIndex = state.Index;
                    ApplyMultiplayerState(state);
                }

                var interval = _isMultiplayerSessionActive || IsMultiplayerPageActive
                    ? MultiplayerActivePollInterval
                    : MultiplayerIdlePollInterval;
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Terracotta may be shutting down or restarting; retry on the next tick.
                try
                {
                    await Task.Delay(MultiplayerIdlePollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void ApplyMultiplayerState(FrontendTerracottaState state)
    {
        if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyMultiplayerState(state));
            return;
        }

        // Observe rooms created elsewhere (attached instance) so the action buttons stay usable.
        if (state.State is "host-starting" or "host-ok" or "guest-connecting" or "guest-starting" or "guest-ok")
        {
            _isMultiplayerSessionActive = true;
        }

        _multiplayerCurrentState = state.State;
        MultiplayerRoomCode = state.Room is { Length: > 0 } room && state.State is not "waiting"
            ? room
            : null;
        MultiplayerDirectAddress = state.State == "guest-ok" ? state.Url : null;
        MultiplayerMembers = state.State is "host-ok" or "guest-ok"
            ? BuildMultiplayerMembers(state.Profiles)
            : [];
        _isMultiplayerSessionActive = state.State is not ("waiting" or "exception");

        MultiplayerStatusText = BuildMultiplayerStatusText(state.State, state.Url, state.Difficulty);
        MultiplayerStatusDetail = BuildMultiplayerStatusDetail(state.State);
        MultiplayerErrorText = state.State == "exception" ? BuildMultiplayerErrorText(state.ErrorType) : null;

        NotifyMultiplayerAvailabilityChanged();
    }

    private string BuildMultiplayerStatusText(string state, string? directAddress, string? difficulty = null)
    {
        return state switch
        {
            "waiting" => LT("shell.multiplayer.status.ready"),
            "host-scanning" => LT("shell.multiplayer.status.host_scanning"),
            "host-starting" => LT("shell.multiplayer.status.host_starting"),
            "host-ok" => LT("shell.multiplayer.status.host_ok"),
            "guest-connecting" => LT("shell.multiplayer.status.guest_connecting"),
            "guest-starting" => LT(
                "shell.multiplayer.status.guest_starting",
                ("difficulty", ResolveMultiplayerDifficultyText(difficulty))),
            "guest-ok" => string.IsNullOrEmpty(directAddress)
                ? LT("shell.multiplayer.status.guest_ok")
                : LT("shell.multiplayer.status.guest_ok_with_address", ("address", directAddress)),
            "exception" => LT("shell.multiplayer.status.exception"),
            _ => LT("shell.multiplayer.status.ready")
        };
    }

    private string BuildMultiplayerStatusDetail(string state)
    {
        return state switch
        {
            "host-scanning" or "host-starting" or "host-ok" => LT("shell.multiplayer.hint.host"),
            "guest-ok" => LT("shell.multiplayer.hint.guest"),
            _ => string.Empty
        };
    }

    private string BuildMultiplayerErrorText(int errorType)
    {
        var key = errorType is >= 0 and <= 5
            ? $"shell.multiplayer.error.types.{errorType}"
            : "shell.multiplayer.error.types.unknown";
        return LT(key);
    }

    private string ResolveMultiplayerDifficultyText(string? difficulty)
    {
        return difficulty switch
        {
            "EASIEST" => LT("shell.multiplayer.difficulty.easiest"),
            "SIMPLE" => LT("shell.multiplayer.difficulty.simple"),
            "MEDIUM" => LT("shell.multiplayer.difficulty.medium"),
            "TOUGH" => LT("shell.multiplayer.difficulty.tough"),
            _ => LT("shell.multiplayer.difficulty.unknown")
        };
    }

    private IReadOnlyList<MultiplayerMemberViewModel> BuildMultiplayerMembers(
        IReadOnlyList<FrontendTerracottaProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return [];
        }

        var members = new List<MultiplayerMemberViewModel>(profiles.Count);
        foreach (var profile in profiles)
        {
            var kindKey = profile.Kind switch
            {
                "HOST" => "shell.multiplayer.member.kind.host",
                "GUEST" => "shell.multiplayer.member.kind.guest",
                _ => "shell.multiplayer.member.kind.local"
            };

            members.Add(new MultiplayerMemberViewModel(
                string.IsNullOrWhiteSpace(profile.Name) ? profile.Vendor : profile.Name,
                LT(kindKey),
                profile.Kind == "LOCAL"));
        }

        return members;
    }

    private void ShowMultiplayerFailure(Exception exception)
    {
        var message = exception switch
        {
            HttpRequestException => LT("shell.multiplayer.error.network"),
            TimeoutException => LT("shell.multiplayer.error.timeout"),
            _ => exception.Message
        };

        MultiplayerErrorText = LT("shell.multiplayer.error.prefix", ("message", message));
        MultiplayerStatusText = LT("shell.multiplayer.status.exception");
    }

    private void NotifyMultiplayerAvailabilityChanged()
    {
        RaisePropertyChanged(nameof(CanStartMultiplayerHosting));
        RaisePropertyChanged(nameof(CanJoinMultiplayerRoom));
        RaisePropertyChanged(nameof(CanLeaveMultiplayerRoom));
        RaisePropertyChanged(nameof(ShowMultiplayerJoinInput));
        RaisePropertyChanged(nameof(ShowMultiplayerRoomCode));
        RaisePropertyChanged(nameof(ShowMultiplayerMembers));
        RaisePropertyChanged(nameof(ShowMultiplayerDirectAddress));
    }
}
