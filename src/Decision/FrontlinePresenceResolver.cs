namespace ai02;

internal static class FrontlinePresenceResolver
{
    internal static bool Resolve(
        bool scoreReaderIsInFrontline,
        bool latestSnapshotIsInFrontline,
        bool latestSnapshotMatchesCurrentMap,
        bool hasKnownFrontlineMap)
        => scoreReaderIsInFrontline
            || hasKnownFrontlineMap
            || (latestSnapshotMatchesCurrentMap && latestSnapshotIsInFrontline);
}
