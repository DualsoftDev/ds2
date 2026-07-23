// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
using DSPilot.Services.CloudAuth;
using Xunit;

namespace DSPilot.Tests;

public sealed class CloudAuthPasswordPolicyTests
{
    [Theory]
    [InlineData("Abcdef1!", true)]
    [InlineData("Ab1!", false)]
    [InlineData("abcdef1!", false)]
    [InlineData("ABCDEF1!", false)]
    [InlineData("Abcdefg!", false)]
    [InlineData("Abcdef12", false)]
    public void MatchesPi5WizardPolicy(string password, bool expected)
    {
        Assert.Equal(expected, PasswordPolicy.IsValid(password));
    }
}
