using Bastion.App.ViewModels;
using Bastion.Core;

namespace Bastion.App.Tests.Explorer;

/// <summary>
/// The back and forward history. It behaves like a browser, not like a stack of parents, and it is
/// bounded so a long session cannot pin folder ids for ever.
/// </summary>
public sealed class NavigationHistoryTests
{
    [Fact]
    public void AFreshHistoryGoesNowhere()
    {
        var history = new NavigationHistory();

        Assert.Null(history.Current);
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back());
        Assert.Null(history.Forward());
    }

    [Fact]
    public void BackAndForwardWalkTheVisitedPlaces()
    {
        var history = new NavigationHistory();
        history.Visit(new EntryId(1));
        history.Visit(new EntryId(2));
        history.Visit(new EntryId(3));

        Assert.Equal(new EntryId(2), history.Back());
        Assert.Equal(new EntryId(1), history.Back());
        Assert.False(history.CanGoBack);
        Assert.Equal(new EntryId(2), history.Forward());
        Assert.Equal(new EntryId(3), history.Forward());
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void VisitingSomewhereNewDropsTheForwardBranch()
    {
        var history = new NavigationHistory();
        history.Visit(new EntryId(1));
        history.Visit(new EntryId(2));
        history.Back();

        history.Visit(new EntryId(9));

        Assert.False(history.CanGoForward);
        Assert.Equal(new EntryId(9), history.Current);
        Assert.Equal([new EntryId(1), new EntryId(9)], history.Places);
    }

    [Fact]
    public void RevisitingTheCurrentPlaceIsNotRecorded()
    {
        var history = new NavigationHistory();
        history.Visit(new EntryId(4));
        history.Visit(new EntryId(4));

        Assert.Single(history.Places);
    }

    [Fact]
    public void TheHistoryIsBoundedAtSixtyFour()
    {
        var history = new NavigationHistory();
        for (uint i = 1; i <= 100; i++)
        {
            history.Visit(new EntryId(i));
        }

        Assert.Equal(NavigationHistory.Capacity, history.Places.Count);
        Assert.Equal(new EntryId(100), history.Current);
        Assert.Equal(new EntryId(37), history.Places[0]);
    }

    [Fact]
    public void ClearEmptiesEverything()
    {
        var history = new NavigationHistory();
        history.Visit(new EntryId(1));
        history.Visit(new EntryId(2));

        history.Clear();

        Assert.Empty(history.Places);
        Assert.Null(history.Current);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void PruneDropsFoldersThatNoLongerExist()
    {
        var history = new NavigationHistory();
        history.Visit(new EntryId(1));
        history.Visit(new EntryId(2));
        history.Visit(new EntryId(3));

        history.Prune(id => id.Value != 2);

        Assert.Equal([new EntryId(1), new EntryId(3)], history.Places);
        Assert.Equal(new EntryId(3), history.Current);
    }

    [Fact]
    public void PruneKeepsTheCursorOnASurvivingPlace()
    {
        var history = new NavigationHistory();
        history.Visit(new EntryId(1));
        history.Visit(new EntryId(2));
        history.Visit(new EntryId(3));
        history.Back();

        history.Prune(id => id.Value == 1);

        Assert.Equal(new EntryId(1), history.Current);
        Assert.False(history.CanGoForward);
    }
}
