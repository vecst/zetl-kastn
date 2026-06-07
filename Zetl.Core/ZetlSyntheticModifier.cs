namespace ZETL;

internal static class ZetlSyntheticModifier
{
    public static int? SelectInjection(
        bool leftDown,
        bool rightDown,
        int leftKey,
        int rightKey)
    {
        if (leftDown && rightDown)
        {
            return null;
        }

        return leftDown ? rightKey : leftKey;
    }
}
