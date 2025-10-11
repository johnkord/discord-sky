namespace DiscordSky.Bot.Models;

public sealed record HeckleTrigger(string Username, string Declaration, DateTimeOffset Timestamp, bool Delivered);

public sealed record HeckleResponse(string Reminder, string FollowUpCelebration, DateTimeOffset NextNudgeAt);
