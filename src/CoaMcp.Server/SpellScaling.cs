namespace CoaMcp.Server;

/// <summary>Port of Spellwork's SpellScalingCalculator: the 3.3.5 SpellEffectInfo::CalcValue rule without caster-only terms.</summary>
public static class SpellScaling
{
    public const int MaxLevel = 100;

    /// <summary>
    /// 1. Only when realPointsPerLevel != 0: level = casterLevel; above MaxLevel (when MaxLevel &gt; 0) it becomes MaxLevel,
    ///    else below BaseLevel it becomes BaseLevel (if / else-if, never both); then level -= max(BaseLevel, SpellLevel) and
    ///    basePoints += (int)(level * realPointsPerLevel), the float product truncated toward zero.
    /// 2. DieSides 0 adds nothing. Otherwise the rolls run from basePoints + 1 to basePoints + dieSides (either order).
    /// 3. Combo points are reported separately, not folded in.
    /// Base is the roll of 1.
    /// </summary>
    public static (float Base, float Min, float Max, float PerComboPoint) Value(
        int basePoints, int dieSides, float realPointsPerLevel, float pointsPerComboPoint,
        int spellLevel, int baseLevel, int maxLevel, int casterLevel)
    {
        if (realPointsPerLevel != 0.0f)
        {
            var level = casterLevel;
            if (level > maxLevel && maxLevel > 0) level = maxLevel;
            else if (level < baseLevel) level = baseLevel;

            level -= Math.Max(baseLevel, spellLevel);
            basePoints += (int)(level * realPointsPerLevel);
        }

        if (dieSides == 0)
            return (basePoints, basePoints, basePoints, pointsPerComboPoint);

        var roll1 = basePoints + 1;
        var rollDie = basePoints + dieSides;
        return (roll1, Math.Min(roll1, rollDie), Math.Max(roll1, rollDie), pointsPerComboPoint);
    }
}
