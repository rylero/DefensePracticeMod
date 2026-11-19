using UnityEngine;

namespace BumpDefenseMod
{
    public enum DefenseLevel
    {
        Easy,
        Medium,
        Hard,
        Brutal
    }

    public enum ShotContest
    {
        Off,
        Normal,
        Aggressive
    }

    /// <summary>
    /// Practice-menu settings for the bump-D NPC. Stored in PlayerPrefs so they survive play-mode.
    /// </summary>
    public static class DefenseDifficulty
    {
        private const string LevelKey = "MoSim.Defense.Level";
        private const string ShotKey = "MoSim.Defense.ShotContest";
        private const string SpeedKey = "MoSim.Defense.Speed";

        public static DefenseLevel Level { get; private set; } = DefenseLevel.Medium;
        public static ShotContest Shot { get; private set; } = ShotContest.Normal;
        public static float Speed { get; private set; } = 1f;
        public static int Revision { get; private set; }

        static DefenseDifficulty()
        {
            Load();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RetainNpcTypes()
        {
            Load();
            _ = typeof(HubBumpDefenseAutopilot);
            _ = typeof(HubBumpDefenseDriver);
        }

        public static void SetLevel(DefenseLevel level)
        {
            if (Level == level) return;
            Level = level;
            Revision++;
            Save();
        }

        public static void SetShot(ShotContest shot)
        {
            if (Shot == shot) return;
            Shot = shot;
            Revision++;
            Save();
        }

        public static void SetSpeed(float speed)
        {
            speed = Mathf.Clamp(speed, 0.5f, 1.25f);
            if (Mathf.Abs(Speed - speed) < 0.01f) return;
            Speed = speed;
            Revision++;
            Save();
        }

        public static void Load()
        {
            Level = (DefenseLevel)Mathf.Clamp(PlayerPrefs.GetInt(LevelKey, (int)DefenseLevel.Medium), 0, 3);
            Shot = (ShotContest)Mathf.Clamp(PlayerPrefs.GetInt(ShotKey, (int)ShotContest.Normal), 0, 2);
            Speed = Mathf.Clamp(PlayerPrefs.GetFloat(SpeedKey, 1f), 0.5f, 1.25f);
        }

        public static void Save()
        {
            PlayerPrefs.SetInt(LevelKey, (int)Level);
            PlayerPrefs.SetInt(ShotKey, (int)Shot);
            PlayerPrefs.SetFloat(SpeedKey, Speed);
            PlayerPrefs.Save();
        }

        public static string LevelLabel(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => "Easy",
                DefenseLevel.Hard => "Hard",
                DefenseLevel.Brutal => "Brutal",
                _ => "Medium"
            };
        }

        public static string ShotLabel(ShotContest shot)
        {
            return shot switch
            {
                ShotContest.Off => "Off",
                ShotContest.Aggressive => "Aggro",
                _ => "On"
            };
        }

        public static string Summary()
        {
            return $"{LevelLabel(Level)}  shot {ShotLabel(Shot)}  speed {Speed:0.00}x";
        }

        public static string LevelHint()
        {
            return Level switch
            {
                DefenseLevel.Easy => "Slow reads, bites on feints, light contact.",
                DefenseLevel.Hard => "Faster reads, stronger ram, shorter retreat.",
                DefenseLevel.Brutal => "Almost no delay. Pins hard and stays on the bump.",
                _ => "Default bump D. Screens, wedges, and rams shots."
            };
        }

        public static float SampleReactionDelay() => SampleReactionDelay(Level);
        public static float SampleCommitSeconds() => SampleCommitSeconds(Level);
        public static float SampleLookahead() => SampleLookahead(Level);
        public static float SampleBiteChance() => SampleBiteChance(Level);
        public static float PinSeconds() => PinSeconds(Level);
        public static float RetreatSeconds() => RetreatSeconds(Level);
        public static float RamScale() => RamScale(Level, Shot);
        public static float DriveScale() => DriveScale(Level, Speed);
        public static float WedgeSitSeconds() => WedgeSitSeconds(Level);
        public static float WedgeLockoutSeconds() => WedgeLockoutSeconds(Level);
        public static float ShootSpeedThreshold() => ShootSpeedThreshold(Level, Shot);

        public static float SampleReactionDelay(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => Random.Range(0.50f, 0.78f),
                DefenseLevel.Hard => Random.Range(0.12f, 0.22f),
                DefenseLevel.Brutal => Random.Range(0.04f, 0.12f),
                _ => Random.Range(0.22f, 0.40f)
            };
        }

        public static float SampleCommitSeconds(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => Random.Range(1.25f, 1.90f),
                DefenseLevel.Hard => Random.Range(0.45f, 0.75f),
                DefenseLevel.Brutal => Random.Range(0.28f, 0.50f),
                _ => Random.Range(0.70f, 1.20f)
            };
        }

        public static float SampleLookahead(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => Random.Range(0.12f, 0.24f),
                DefenseLevel.Hard => Random.Range(0.42f, 0.62f),
                DefenseLevel.Brutal => Random.Range(0.55f, 0.78f),
                _ => Random.Range(0.28f, 0.48f)
            };
        }

        public static float SampleBiteChance(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => Random.Range(0.55f, 0.78f),
                DefenseLevel.Hard => Random.Range(0.08f, 0.18f),
                DefenseLevel.Brutal => Random.Range(0.02f, 0.08f),
                _ => Random.Range(0.22f, 0.38f)
            };
        }

        public static float PinSeconds(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => 1.6f,
                DefenseLevel.Hard => 3.6f,
                DefenseLevel.Brutal => 4.8f,
                _ => 3f
            };
        }

        public static float RetreatSeconds(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => 4.2f,
                DefenseLevel.Hard => 2.2f,
                DefenseLevel.Brutal => 1.4f,
                _ => 3f
            };
        }

        public static float RamScale(DefenseLevel level, ShotContest shot)
        {
            var scale = level switch
            {
                DefenseLevel.Easy => 0.48f,
                DefenseLevel.Hard => 1.18f,
                DefenseLevel.Brutal => 1.35f,
                _ => 1f
            };
            if (shot == ShotContest.Off) return 0f;
            if (shot == ShotContest.Aggressive) scale *= 1.12f;
            return scale;
        }

        public static float DriveScale(DefenseLevel level, float speed)
        {
            var scale = level switch
            {
                DefenseLevel.Easy => 0.62f,
                DefenseLevel.Hard => 1.05f,
                DefenseLevel.Brutal => 1.18f,
                _ => 0.92f
            };
            return Mathf.Clamp(scale * speed, 0.4f, 1.35f);
        }

        public static float WedgeSitSeconds(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => 2.4f,
                DefenseLevel.Hard => 5.0f,
                DefenseLevel.Brutal => 6.5f,
                _ => 4f
            };
        }

        public static float WedgeLockoutSeconds(DefenseLevel level)
        {
            return level switch
            {
                DefenseLevel.Easy => 2.8f,
                DefenseLevel.Hard => 1.1f,
                DefenseLevel.Brutal => 0.55f,
                _ => 1.6f
            };
        }

        public static float ShootSpeedThreshold(DefenseLevel level, ShotContest shot)
        {
            if (shot == ShotContest.Aggressive)
            {
                return level == DefenseLevel.Brutal ? 2.6f : 2.1f;
            }

            return level switch
            {
                DefenseLevel.Easy => 0.75f,
                DefenseLevel.Hard => 1.35f,
                DefenseLevel.Brutal => 1.7f,
                _ => 1.15f
            };
        }
    }
}
