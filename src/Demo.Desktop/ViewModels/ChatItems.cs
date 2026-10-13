using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Demo.Desktop.ViewModels;

/// <summary>One visual entry in the chat transcript. Each subtype has a DataTemplate in ChatPanel.xaml.</summary>
public abstract class ChatItem : ObservableObject;

public sealed class UserItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

public sealed class AssistantItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

public sealed class NoticeItem(string text, bool isError = false) : ChatItem
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

/// <summary>A read the assistant ran automatically. Shown so the user can see what the AI looked at.</summary>
public sealed partial class ToolItem(string title, string name) : ChatItem
{
    public string Title { get; } = title;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isDone;
}

public enum ApprovalState { Pending, Approved, Denied }

/// <summary>A change the assistant wants to make. Nothing happens until the user clicks Approve.</summary>
public sealed partial class ApprovalItem : ChatItem
{
    private readonly TaskCompletionSource<bool> _decision = new();

    public ApprovalItem(string title, string details)
    {
        Title = title;
        Details = details;
    }

    public string Title { get; }
    public string Details { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private ApprovalState _state;

    [ObservableProperty] private bool _isDone;

    public bool IsPending => State == ApprovalState.Pending;

    public Task<bool> Decision => _decision.Task;

    [RelayCommand]
    private void Approve() => Decide(true);

    [RelayCommand]
    private void Deny() => Decide(false);

    public void Cancel() => _decision.TrySetCanceled();

    private void Decide(bool approved)
    {
        if (!IsPending) return;
        State = approved ? ApprovalState.Approved : ApprovalState.Denied;
        _decision.TrySetResult(approved);
    }
}
