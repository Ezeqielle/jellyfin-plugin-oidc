using System;
using System.IO;
using Jellyfin.Plugin.OIDC.Api;
using Jellyfin.Plugin.OIDC.Configuration;
using Xunit;

namespace Jellyfin.Plugin.OIDC.Tests;

/// <summary>
/// The login-button script is assembled from string fragments, so the C# build checks nothing
/// about the JavaScript it produces. Two separate changes have now shipped a script that failed
/// to parse, and the failure mode is silent: the browser drops the whole script, every SSO
/// button disappears from the login page, and the server logs nothing.
///
/// <see cref="DelimitersBalance"/> catches that class of break in-process. CI additionally runs
/// `node --check` over the file written by <see cref="RendersScriptArtifact"/>, which is the
/// authoritative check — a real parser rather than a counter.
/// </summary>
public class LoginButtonScriptTests
{
    /// <summary>Name CI looks for under the test output directory.</summary>
    private const string ArtifactName = "login-buttons.generated.js";

    /// <summary>
    /// Deliberately awkward: an apostrophe in the display name exercises the quote escaping,
    /// and parentheses in a provider name would unbalance a naive delimiter count if they ever
    /// leaked out of their string literal.
    /// </summary>
    private static readonly OidcProviderConfig[] Providers =
    {
        new()
        {
            ProviderId = "authentik",
            DisplayName = "authentik",
            ButtonColor = "#fd4b2d",
            Enabled = true
        },
        new()
        {
            ProviderId = "keycloak",
            DisplayName = "Bob's IdP (staging)",
            ButtonColor = "#4285F4",
            Enabled = true
        }
    };

    [Fact]
    public void EveryProviderGetsBothLinks()
    {
        var script = LoginButtonController.BuildScript(Providers);

        foreach (var provider in Providers)
        {
            Assert.Contains($"/sso/OIDC/Start/{provider.ProviderId}", script, StringComparison.Ordinal);
            Assert.Contains($"/sso/OIDC/QuickConnect/{provider.ProviderId}", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnlyAttachesToTheLoginForm()
    {
        var script = LoginButtonController.BuildScript(Providers);

        // '[data-role="page"] form' matched forms on ordinary pages too — notably
        // '.trackSelections' on item detail pages, which put the SSO banner above the
        // audio/subtitle selectors. See #29.
        Assert.DoesNotContain("data-role", script, StringComparison.Ordinal);
        Assert.Contains(".manualLoginForm", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserverIsDeclaredBeforeUse()
    {
        var script = LoginButtonController.BuildScript(Providers);

        // A `sb`/`ab` typo once emitted `observer.observe(...)` with no declaration. That is
        // valid syntax, so `node --check` would pass it — only a reference check catches it.
        Assert.Contains("var observer = new MutationObserver(", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("var observer =", StringComparison.Ordinal)
            < script.IndexOf("observer.observe(", StringComparison.Ordinal),
            "observer must be declared before it is used");
    }

    [Fact]
    public void RunsOnceImmediatelyAndStopsObservingAfterInserting()
    {
        var script = LoginButtonController.BuildScript(Providers);

        // Without the immediate call the buttons never appear when the form is already in the
        // DOM at injection time, which is the common case for the Branding snippet. Matching
        // the call rather than a whole line keeps this off Environment.NewLine.
        Assert.Contains("addButtons();", script, StringComparison.Ordinal);
        Assert.Contains("observer.disconnect();", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DelimitersBalance()
    {
        var script = LoginButtonController.BuildScript(Providers);

        Assert.Equal(Count(script, '('), Count(script, ')'));
        Assert.Equal(Count(script, '{'), Count(script, '}'));
        Assert.Equal(Count(script, '['), Count(script, ']'));
    }

    /// <summary>
    /// Writes the rendered script next to the test binaries so the CI job can run a real
    /// JavaScript parser over it. Asserting the file is non-empty keeps a silently skipped
    /// write from letting the CI step pass on a stale artifact.
    /// </summary>
    [Fact]
    public void RendersScriptArtifact()
    {
        var script = LoginButtonController.BuildScript(Providers);
        var path = Path.Combine(AppContext.BaseDirectory, ArtifactName);

        File.WriteAllText(path, script);

        Assert.True(File.Exists(path));
        Assert.NotEmpty(File.ReadAllText(path));
    }

    private static int Count(string value, char c)
    {
        var total = 0;
        foreach (var ch in value)
        {
            if (ch == c)
            {
                total++;
            }
        }

        return total;
    }
}
