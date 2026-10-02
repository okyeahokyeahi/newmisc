namespace DesktopBuddy.Magi;

public enum Core { Melchior, Balthasar, Casper }

/// <summary>One core's vote and the fact it's based on (from Desktop Buddy's own checks).</summary>
public sealed record CoreVote(bool Approve, string Fact);

/// <summary>A button under the verdict. The recommended action is Primary.</summary>
public sealed record MagiAction(string Label, Action Run, bool Primary = false);

/// <summary>A decision put to the three cores. The votes are rule-based; the AI only gives them a voice.</summary>
public sealed class MagiCase
{
    public required string Question { get; init; }   // typed into the question field
    public required string Proposal { get; init; }   // big caption, e.g. "TERMINATE svch0st.exe ?"
    public required string Context { get; init; }    // one line of facts
    public required string Code { get; init; }
    public required CoreVote Melchior { get; init; }  // scientist: performance and hardware
    public required CoreVote Balthasar { get; init; } // mother: you and the laptop's health
    public required CoreVote Casper { get; init; }    // woman: security
    public required string IfApproved { get; init; }
    public required string IfDenied { get; init; }
    public required IReadOnlyList<MagiAction> Actions { get; init; }
    public bool Security { get; init; }

    public CoreVote Vote(Core c) => c switch { Core.Melchior => Melchior, Core.Balthasar => Balthasar, _ => Casper };
    public int Yes => new[] { Melchior, Balthasar, Casper }.Count(v => v.Approve);
    public bool Passed => Yes >= 2;
}
