using System.Numerics;
using Xunit;

namespace ai02.Tests;

public sealed class CommandOverlayAiDisplayPolicyTests
{
    [Fact]
    public void IsAiLead_ReturnsTrueForAiActionIds()
    {
        var aiCommand = BattlefieldTestFactory.Command(
            "ai:command:1:Rotate:safe",
            BattlefieldCommandKind.Rotate,
            "主团转点 Safe Node",
            new Vector3(90f, 0f, 70f),
            "Safe Node");
        var aiAction = BattlefieldTestFactory.Action(
            "ai:action:1:Rotate:safe",
            aiCommand.Id,
            BattlefieldActionType.Rotate,
            BattlefieldCommandKind.Rotate,
            "主团转点 Safe Node",
            new Vector3(90f, 0f, 70f),
            "safe",
            "Safe Node");
        var decision = new BattlefieldDecisionSnapshot
        {
            IsAvailable = true,
            CommandSituation = new BattlefieldCommandSituationSnapshot
            {
                IsAvailable = true,
                PrimaryCommand = aiCommand,
                PrimaryAction = aiAction,
                Publish = new BattlefieldCommandPublishSnapshot
                {
                    PriorityText = "AI 主导",
                    Command = aiCommand
                }
            },
            PrimaryAction = aiAction,
            PublishedAction = aiAction,
            SummaryText = "AI 接管：主团转点 Safe Node"
        };

        Assert.True(CommandOverlayAiDisplayPolicy.IsAiLead(decision));
    }

    [Fact]
    public void ResolveDisplay_KeepsLastAiDirectiveWithinHoldWindow()
    {
        CommandOverlayDirectiveDisplaySnapshot? heldAi = null;
        long heldAiTicks = -1;
        var aiDisplay = new CommandOverlayDirectiveDisplaySnapshot(
            "AI 转点 Safe Node",
            "AI 抢高价值点",
            true);

        var first = CommandOverlayAiDisplayPolicy.ResolveDisplay(
            aiDisplay,
            1000,
            5,
            ref heldAi,
            ref heldAiTicks);

        Assert.True(first.IsAiLead);
        Assert.Equal("AI 转点 Safe Node", first.PrimaryCommandLine);

        var localDisplay = new CommandOverlayDirectiveDisplaySnapshot(
            "本地抢点 Danger Node",
            "本地断摸 Danger Node",
            false);

        var sticky = CommandOverlayAiDisplayPolicy.ResolveDisplay(
            localDisplay,
            6000,
            5,
            ref heldAi,
            ref heldAiTicks);

        Assert.True(sticky.IsAiLead);
        Assert.Equal("AI 转点 Safe Node", sticky.PrimaryCommandLine);
        Assert.Equal("AI 抢高价值点", sticky.CurrentActionLine);
    }

    [Fact]
    public void ResolveDisplay_ReleasesAiDirectiveAfterHoldWindowExpires()
    {
        CommandOverlayDirectiveDisplaySnapshot? heldAi = new(
            "AI 打第一 Enemy1",
            "AI 接团 Enemy1",
            true);
        long heldAiTicks = 1000;
        var localDisplay = new CommandOverlayDirectiveDisplaySnapshot(
            "本地抢点 Danger Node",
            "本地断摸 Danger Node",
            false);

        var resolved = CommandOverlayAiDisplayPolicy.ResolveDisplay(
            localDisplay,
            6001,
            5,
            ref heldAi,
            ref heldAiTicks);

        Assert.False(resolved.IsAiLead);
        Assert.Equal("本地抢点 Danger Node", resolved.PrimaryCommandLine);
        Assert.Null(heldAi);
        Assert.Equal(-1, heldAiTicks);
    }
}
