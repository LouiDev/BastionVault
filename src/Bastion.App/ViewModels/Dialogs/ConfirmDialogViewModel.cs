using Bastion.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace Bastion.App.ViewModels.Dialogs;

/// <summary>What a message dialog is saying.</summary>
public enum MessageKind
{
    /// <summary>A choice the user has to make.</summary>
    Question,

    /// <summary>Something the user only has to acknowledge.</summary>
    Information,

    /// <summary>Something went wrong.</summary>
    Error,
}

/// <summary>
/// The confirmation, information and error dialog. The title is a verb plus a count, the buttons
/// are verbs, and a destructive primary is styled danger and is never the default: the user has to
/// aim at it (UI-CONTRACT.md section 7).
/// </summary>
public sealed partial class ConfirmDialogViewModel : DialogViewModelBase<ConfirmResult>
{
    /// <summary>Creates a confirmation from a request.</summary>
    /// <param name="request">What to ask.</param>
    /// <param name="kind">Whether this is a question, a notice or a failure.</param>
    public ConfirmDialogViewModel(ConfirmRequest request, MessageKind kind = MessageKind.Question)
    {
        ArgumentNullException.ThrowIfNull(request);

        Title = request.Title;
        Body = request.Body;
        PrimaryVerb = request.PrimaryVerb;
        CancelVerb = request.CancelVerb;
        SecondaryVerb = request.SecondaryVerb;
        IsDestructive = request.IsDestructive;
        Detail = request.Detail;
        Kind = kind;
    }

    /// <summary>The one or two sentences under the title.</summary>
    public string Body { get; }

    /// <summary>Label of the affirmative button.</summary>
    public string PrimaryVerb { get; }

    /// <summary>Label of the dismissing button.</summary>
    public string CancelVerb { get; }

    /// <summary>Label of the optional third button.</summary>
    public string? SecondaryVerb { get; }

    /// <summary>True when the third button exists.</summary>
    public bool HasSecondary => !string.IsNullOrEmpty(SecondaryVerb);

    /// <summary>True when the primary verb destroys something.</summary>
    public bool IsDestructive { get; }

    /// <summary>Optional monospaced detail (a path, a count, a KDF summary).</summary>
    public string? Detail { get; }

    /// <summary>True when there is a detail block to show.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>What the dialog is saying, which drives the glyph and its colour.</summary>
    public MessageKind Kind { get; }

    /// <summary>True when the dismissing button should be hidden (an information dialog).</summary>
    public bool HasCancel => Kind == MessageKind.Question;

    /// <summary>Accepts the primary verb.</summary>
    [RelayCommand]
    public void Primary() => Close(ConfirmResult.Primary);

    /// <summary>Accepts the secondary verb.</summary>
    [RelayCommand]
    public void Secondary() => Close(ConfirmResult.Secondary);

    /// <inheritdoc />
    public override bool Accept()
    {
        // Enter takes the primary verb only when it is safe; a destructive verb must be aimed at.
        if (IsDestructive)
        {
            return false;
        }

        Close(ConfirmResult.Primary);
        return true;
    }
}
