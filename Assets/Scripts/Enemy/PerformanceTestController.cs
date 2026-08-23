using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Configures an EnemySpawner and the live PlayerWeaponSystem for a repeatable
/// pooling-versus-no-pooling performance test. Attach this component to the
/// PerformanceTest scene. Pooling is toggled through the single master switch
/// on ObjectPoolManager (poolingEnabled), so this compares the real gameplay
/// path with pooling on vs off rather than two different code paths.
///
/// Two things matter for the comparison to mean anything:
///
/// 1. Throughput. EnemySpawner.SpawnEnemies() spawns at most one enemy per
///    EnemyGroup per interval, so a single group at a 0.1s interval produces
///    only ~10 objects/second — far too little churn for pooling to show up
///    above measurement noise. enemiesPerSpawnBatch splits the wave across
///    several groups so each interval spawns that many enemies at once.
///
/// 2. The metric. Object pooling reduces memory allocation and therefore GC
///    pressure; it does not make a frame that was already comfortably inside
///    budget any faster. Average FPS is the wrong instrument — with VSync on,
///    both runs simply report the refresh rate. This component therefore also
///    records the 1% low FPS (the worst frames, which is where GC pauses land)
///    and the true per-frame GC allocation.
///
/// Allocation is read from the Unity profiler's "GC Allocated In Frame"
/// counter via ProfilerRecorder — the same source as the GC.Alloc column in the
/// Profiler window, and it works without the Profiler window being open.
/// System.GC.CollectionCount() and System.GC.GetTotalMemory() are NOT used as
/// the primary figures: Unity's scripting runtime uses the Boehm collector, not
/// the .NET generational one, so those .NET APIs do not report collections
/// meaningfully here.
/// </summary>
public class PerformanceTestController : MonoBehaviour
{
    [Header("Scenario")]
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] private int testEnemyCount = 3000;
    [SerializeField] private int maxConcurrentEnemies = 300;
    [SerializeField] private float testDuration = 30f;
    [SerializeField] private float spawnInterval = 0.05f;

    [Tooltip("Enemies spawned per spawn interval. The wave is split into this " +
             "many EnemyGroups because the spawner emits one enemy per group " +
             "per interval. Raise this until object churn is high enough for " +
             "pooling to be measurable.")]
    [SerializeField] private int enemiesPerSpawnBatch = 20;

    [SerializeField] private float weaponCooldownOverride = 0.2f;

    [Header("Variable Under Test")]
    [Tooltip("The ONLY value that should differ between two runs being compared.")]
    [SerializeField] private bool usePooling = true;

    [Header("Environment Lock")]
    [Tooltip("Forces VSync off and a fixed frame-rate cap so both runs are " +
             "measured under identical conditions. Leave enabled.")]
    [SerializeField] private bool lockEnvironment = true;
    [SerializeField] private int targetFrameRateCap = -1;

    [Tooltip("Seconds of warm-up discarded before measurement starts, so " +
             "shader compilation and first-frame costs do not land in the data.")]
    [SerializeField] private float warmupDuration = 3f;

    [Tooltip("Draw the live HUD during the measured window. Unity's IMGUI " +
             "allocates every frame (OnGUI runs twice per frame), so leaving " +
             "this ON adds a constant to the GC.Alloc figure. Turn it OFF for " +
             "recorded runs — the result still appears on screen and in the " +
             "Console when the run finishes.")]
    [SerializeField] private bool drawHudDuringRun = false;

    private EnemySpawner spawner;
    private PlayerWeaponSystem playerWeaponSystem;

    private float elapsed;
    private float warmupElapsed;
    private bool warmingUp;
    private bool testRunning;

    private float minFps = float.MaxValue;
    private float maxFps;
    private float avgFps;
    private float onePercentLowFps;
    private float currentFps;

    private List<float> frameTimes;

    /// <summary>
    /// Reads the profiler's "GC Allocated In Frame" counter — bytes allocated
    /// on the managed heap during the previous frame. This is the authoritative
    /// GC.Alloc figure and needs no Profiler window attached, but it requires
    /// ENABLE_PROFILER (always on in the Editor and in a Development Build).
    /// </summary>
    private ProfilerRecorder gcAllocRecorder;
    private bool gcRecorderAvailable;

    private long allocatedBytes;
    private long lastHeapSize;
    private int gcCollectionsAtStart;
    private int gcCollections;

    /// <summary>
    /// Allocation divided by measured frames. Reported directly so the result
    /// can be transcribed into a "GC.Alloc / frame" column without arithmetic.
    /// </summary>
    private float AllocPerFrameKb =>
        frameTimes == null || frameTimes.Count == 0
            ? 0f
            : allocatedBytes / 1024f / frameTimes.Count;

    private float cooldownOverrideTimer;
    private int peakConcurrentEnemies;

    // Cached HUD strings. Rebuilding these every OnGUI would allocate an
    // object[], a boxed float and a string per label, twice per frame.
    private readonly string[] hudLines = new string[9];
    private float hudRefreshTimer;

    private void OnEnable()
    {
        gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        gcRecorderAvailable = gcAllocRecorder.Valid;

        if (!gcRecorderAvailable)
        {
            Debug.LogWarning(
                "PerformanceTest: the 'GC Allocated In Frame' profiler counter is unavailable " +
                "(profiling is disabled in this build). Falling back to System.GC.GetTotalMemory, " +
                "which under Unity's Boehm collector only approximates allocation. Run in the " +
                "Editor or in a Development Build for the accurate figure.");
        }
    }

    private void OnDisable()
    {
        if (gcAllocRecorder.Valid)
        {
            gcAllocRecorder.Dispose();
        }
    }

    private void Awake()
    {
        // EnemySpawner.Start rejects an empty wave list. Supply a harmless
        // placeholder so it can initialize its player and spawn-point state
        // before this controller installs the actual test wave.
        EnemySpawner sceneSpawner = FindAnyObjectByType<EnemySpawner>();
        if (sceneSpawner == null || sceneSpawner.waves != null && sceneSpawner.waves.Count > 0)
        {
            return;
        }

        sceneSpawner.waves = new List<EnemySpawner.Wave>
        {
            new EnemySpawner.Wave
            {
                waveName = "Performance Test Initialization",
                enemyGroups = new List<EnemySpawner.EnemyGroup>(),
                waveQuota = 0,
                spawnInterval = 0f,
                spawnCount = 0
            }
        };
    }

    private void Start()
    {
        ApplyEnvironmentLock();
        StartCoroutine(InitAfterOneFrame());
    }

    /// <summary>
    /// Removes the two settings that most often make a pooled and an unpooled
    /// run look identical: VSync (both runs get capped at the refresh rate, so
    /// the difference disappears) and an uncapped-but-varying frame rate.
    /// </summary>
    private void ApplyEnvironmentLock()
    {
        if (!lockEnvironment)
        {
            return;
        }

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = targetFrameRateCap;
    }

    private IEnumerator InitAfterOneFrame()
    {
        // Let EnemySpawner.Start initialize its player and spawn-point references first.
        yield return null;

        ConfigureSpawner();
    }

    private void ConfigureSpawner()
    {
        spawner = FindAnyObjectByType<EnemySpawner>();
        if (spawner == null)
        {
            Debug.LogError("PerformanceTest: EnemySpawner not found in the scene.");
            return;
        }

        if (enemyPrefab == null)
        {
            Debug.LogError("PerformanceTest: Assign an enemy prefab before running the test.");
            return;
        }

        if (ObjectPoolManager.Instance != null)
        {
            ObjectPoolManager.Instance.poolingEnabled = usePooling;
        }
        else
        {
            Debug.LogWarning("PerformanceTest: no ObjectPoolManager in the scene — pooling toggle has no effect and every spawn falls back to Instantiate/Destroy.");
        }

        // The spawner carries its own pooling flag. Force it on so the single
        // master switch on ObjectPoolManager is genuinely the only variable.
        spawner.useObjectPooling = true;

        playerWeaponSystem = FindAnyObjectByType<PlayerWeaponSystem>();
        if (playerWeaponSystem == null)
        {
            Debug.LogWarning("PerformanceTest: no PlayerWeaponSystem in the scene — only enemy pooling is being tested, not weapon projectile/effect pooling.");
        }

        int enemyCount = Mathf.Max(0, testEnemyCount);
        spawner.maxEnemiesAllowed = Mathf.Max(1, maxConcurrentEnemies);
        spawner.waveInterval = 0f;

        EnemySpawner.Wave wave = new EnemySpawner.Wave
        {
            waveName = "Stress Test",
            enemyGroups = BuildEnemyGroups(enemyCount),
            waveQuota = enemyCount,
            spawnInterval = Mathf.Max(0f, spawnInterval),
            spawnCount = 0
        };

        spawner.waves = new List<EnemySpawner.Wave> { wave };
        spawner.currentWaveCount = 0;

        // testEnemyCount == 0 is the empty-scene baseline run. Disable the
        // spawner outright rather than leaving it idling: with an exhausted
        // quota it re-enters BeginNextWave() every frame, and each StartCoroutine
        // allocates, which would put a constant allocation into the one run
        // that is supposed to represent zero object activity.
        spawner.enabled = enemyCount > 0;

        ResetMeasurements();
        ApplyCooldownOverride();

        warmingUp = warmupDuration > 0f;
        testRunning = true;
    }

    /// <summary>
    /// Splits the wave quota across enemiesPerSpawnBatch groups. SpawnEnemies()
    /// walks every group once per interval, so N groups means N enemies per
    /// interval instead of one.
    /// </summary>
    private List<EnemySpawner.EnemyGroup> BuildEnemyGroups(int enemyCount)
    {
        int batchSize = Mathf.Max(1, enemiesPerSpawnBatch);
        List<EnemySpawner.EnemyGroup> groups = new List<EnemySpawner.EnemyGroup>(batchSize);

        int remaining = enemyCount;
        for (int i = 0; i < batchSize; i++)
        {
            // Distribute the quota as evenly as possible across the groups.
            int share = remaining / (batchSize - i);
            remaining -= share;

            groups.Add(new EnemySpawner.EnemyGroup
            {
                enemyName = $"Test Enemy {i + 1}",
                enemyCount = share,
                spawnCount = 0,
                enemyPrefab = enemyPrefab,
                isBoss = false,
                isFinalBoss = false
            });
        }

        return groups;
    }

    private void ResetMeasurements()
    {
        elapsed = 0f;
        warmupElapsed = 0f;
        minFps = float.MaxValue;
        maxFps = 0f;
        avgFps = 0f;
        onePercentLowFps = 0f;
        currentFps = 0f;
        cooldownOverrideTimer = 0f;
        peakConcurrentEnemies = 0;
        hudRefreshTimer = 0f;

        // Pre-sized so the list never reallocates mid-run; a resize during
        // measurement would allocate and contaminate the GC figures.
        int expectedFrames = Mathf.CeilToInt(Mathf.Max(1f, testDuration) * 400f);
        frameTimes = new List<float>(expectedFrames);

        allocatedBytes = 0L;
        lastHeapSize = System.GC.GetTotalMemory(false);
        gcCollectionsAtStart = System.GC.CollectionCount(0);
        gcCollections = 0;
    }

    private void Update()
    {
        if (!testRunning)
        {
            return;
        }

        RecountEnemiesAlive();

        cooldownOverrideTimer += Time.deltaTime;
        if (cooldownOverrideTimer >= 0.5f)
        {
            cooldownOverrideTimer = 0f;
            ApplyCooldownOverride();
        }

        float frameTime = Time.unscaledDeltaTime;
        currentFps = frameTime > 0f ? 1f / frameTime : 0f;

        // Discard the warm-up window: shader compilation, the first pool
        // allocations and the first Instantiate of a prefab all land here and
        // would otherwise be charged to whichever mode ran first.
        if (warmingUp)
        {
            warmupElapsed += frameTime;
            if (warmupElapsed < warmupDuration)
            {
                RefreshHudIfVisible(frameTime);
                return;
            }

            warmingUp = false;
            ResetMeasurementCountersAfterWarmup();
            return;
        }

        elapsed += frameTime;
        frameTimes.Add(frameTime);

        minFps = Mathf.Min(minFps, currentFps);
        maxFps = Mathf.Max(maxFps, currentFps);

        SampleMemory();
        RefreshHudIfVisible(frameTime);

        if (elapsed < testDuration)
        {
            return;
        }

        FinishTest();
    }

    /// <summary>
    /// Rebuilds the HUD text at 4 Hz, and only when the HUD is actually on
    /// screen. Skipping it entirely during a hidden measured window keeps the
    /// measurement code's own allocation at zero.
    /// </summary>
    private void RefreshHudIfVisible(float frameTime)
    {
        if (!warmingUp && !drawHudDuringRun)
        {
            return;
        }

        hudRefreshTimer += frameTime;
        if (hudRefreshTimer < 0.25f)
        {
            return;
        }

        hudRefreshTimer = 0f;
        RebuildHudLines();
    }

    private void ResetMeasurementCountersAfterWarmup()
    {
        minFps = float.MaxValue;
        maxFps = 0f;
        frameTimes.Clear();
        allocatedBytes = 0L;
        lastHeapSize = System.GC.GetTotalMemory(false);
        gcCollectionsAtStart = System.GC.CollectionCount(0);
        gcCollections = 0;
    }

    /// <summary>
    /// Plain loop rather than LINQ. A LINQ Count(predicate) call allocates an
    /// enumerator every frame, which would show up in exactly the GC figures
    /// this test is trying to measure.
    /// </summary>
    private void RecountEnemiesAlive()
    {
        int alive = 0;
        List<EnemyStats> active = EnemySpawner.activeEnemies;

        for (int i = 0; i < active.Count; i++)
        {
            EnemyStats enemy = active[i];
            if (enemy != null && enemy.gameObject.activeInHierarchy)
            {
                alive++;
            }
        }

        spawner.enemiesAlive = alive;
        if (alive > peakConcurrentEnemies)
        {
            peakConcurrentEnemies = alive;
        }
    }

    /// <summary>
    /// Accumulates this frame's managed allocation. When the profiler counter is
    /// available it reports exactly what the Profiler's GC.Alloc column shows.
    /// Otherwise it falls back to summing positive heap-size deltas, which under
    /// the Boehm collector understates the true figure — that fallback is why the
    /// warning in OnEnable exists.
    /// </summary>
    private void SampleMemory()
    {
        if (gcRecorderAvailable)
        {
            long frameAllocation = gcAllocRecorder.LastValue;
            if (frameAllocation > 0L)
            {
                allocatedBytes += frameAllocation;
            }

            return;
        }

        long heapSize = System.GC.GetTotalMemory(false);
        if (heapSize > lastHeapSize)
        {
            allocatedBytes += heapSize - lastHeapSize;
        }

        lastHeapSize = heapSize;
        gcCollections = System.GC.CollectionCount(0) - gcCollectionsAtStart;
    }

    private void FinishTest()
    {
        testRunning = false;
        spawner.StopCurrentStage();

        avgFps = CalculateAverageFps();
        onePercentLowFps = CalculateOnePercentLowFps();

        string mode = usePooling ? "WITH POOL" : "NO POOL";
        string allocSource = gcRecorderAvailable ? "profiler" : "FALLBACK-approx";

        Debug.Log(
            $"[PerformanceTest] RESULT | Mode: {mode} | Cap: {maxConcurrentEnemies} | " +
            $"PeakAlive: {peakConcurrentEnemies} | HUD: {(drawHudDuringRun ? "on" : "off")} | " +
            $"AvgFPS: {avgFps:F1} | 1%LowFPS: {onePercentLowFps:F1} | " +
            $"MinFPS: {minFps:F1} | MaxFPS: {maxFps:F1} | " +
            $"GC.Alloc/frame: {AllocPerFrameKb:F2} KB | " +
            $"GC.Alloc total: {allocatedBytes / 1048576f:F1} MB | " +
            $"Frames: {frameTimes.Count} | AllocSource: {allocSource}");

        RebuildHudLines();
    }

    private float CalculateAverageFps()
    {
        if (frameTimes.Count == 0)
        {
            return 0f;
        }

        float total = 0f;
        for (int i = 0; i < frameTimes.Count; i++)
        {
            total += frameTimes[i];
        }

        float meanFrameTime = total / frameTimes.Count;
        return meanFrameTime > 0f ? 1f / meanFrameTime : 0f;
    }

    /// <summary>
    /// Mean FPS of the slowest 1% of frames. This is the figure object pooling
    /// actually moves: pooling removes per-spawn allocation, which removes GC
    /// pauses, which are precisely the isolated slow frames that average FPS
    /// smooths away.
    /// </summary>
    private float CalculateOnePercentLowFps()
    {
        if (frameTimes.Count == 0)
        {
            return 0f;
        }

        // Sorting happens after the run, never during it.
        List<float> sorted = new List<float>(frameTimes);
        sorted.Sort();

        int sampleCount = Mathf.Max(1, Mathf.CeilToInt(sorted.Count * 0.01f));
        float total = 0f;

        // The slowest frames sit at the end of an ascending frame-time sort.
        for (int i = sorted.Count - sampleCount; i < sorted.Count; i++)
        {
            total += sorted[i];
        }

        float meanWorstFrameTime = total / sampleCount;
        return meanWorstFrameTime > 0f ? 1f / meanWorstFrameTime : 0f;
    }

    /// <summary>
    /// Forces the equipped weapon to fire roughly every weaponCooldownOverride
    /// seconds, regardless of its own base cooldown stat, by driving the same
    /// cooldownMultiplier the real upgrade system uses
    /// (definition.Cooldown * cooldownMultiplier == weaponCooldownOverride).
    /// This keeps the stress test on the exact same code path real players use
    /// instead of reflecting into a private field.
    /// </summary>
    private void ApplyCooldownOverride()
    {
        if (playerWeaponSystem == null)
        {
            playerWeaponSystem = FindAnyObjectByType<PlayerWeaponSystem>();
            if (playerWeaponSystem == null)
            {
                return;
            }
        }

        WeaponDefinition definition = WeaponCatalog.Get(playerWeaponSystem.EquippedWeapon);
        float baseCooldown = Mathf.Max(0.01f, definition.Cooldown);
        float overrideCooldown = Mathf.Max(0f, weaponCooldownOverride);

        RunManager.EnsureInstance().Data.cooldownMultiplier = overrideCooldown / baseCooldown;
    }

    /// <summary>
    /// Rebuilds the cached HUD strings. Called at most a few times per second
    /// from Update(), never from OnGUI() — OnGUI runs twice per frame, and each
    /// interpolated string there would allocate an object[], a boxed float and
    /// the resulting string, adding several KB per frame to the very figure this
    /// component is measuring.
    /// </summary>
    private void RebuildHudLines()
    {
        string mode = usePooling ? "WITH POOL" : "NO POOL";

        if (testRunning)
        {
            hudLines[0] = "=== PERFORMANCE TEST ===";
            hudLines[1] = $"Mode: {mode}   Cap: {maxConcurrentEnemies}";
            hudLines[2] = warmingUp
                ? $"WARM-UP {warmupElapsed:F1} / {warmupDuration:F0}s"
                : $"Time: {elapsed:F1} / {testDuration:F0}s";
            hudLines[3] = $"Alive: {(spawner != null ? spawner.enemiesAlive : 0)} (peak {peakConcurrentEnemies})";
            hudLines[4] = $"Current FPS: {currentFps:F1}";
            hudLines[5] = $"Min FPS: {(minFps == float.MaxValue ? 0f : minFps):F1}";
            hudLines[6] = $"GC.Alloc/frame: {AllocPerFrameKb:F2} KB";
            hudLines[7] = $"GC.Alloc total: {allocatedBytes / 1048576f:F1} MB";
            hudLines[8] = gcRecorderAvailable ? "Alloc source: profiler" : "Alloc source: FALLBACK";
            return;
        }

        hudLines[0] = "=== TEST COMPLETE ===";
        hudLines[1] = $"Mode: {mode}   Cap: {maxConcurrentEnemies}";
        hudLines[2] = $"Peak alive: {peakConcurrentEnemies}";
        hudLines[3] = $"Avg FPS: {avgFps:F1}";
        hudLines[4] = $"1% low FPS: {onePercentLowFps:F1}";
        hudLines[5] = $"Min FPS: {(minFps == float.MaxValue ? 0f : minFps):F1}";
        hudLines[6] = $"GC.Alloc/frame: {AllocPerFrameKb:F2} KB";
        hudLines[7] = $"GC.Alloc total: {allocatedBytes / 1048576f:F1} MB";
        hudLines[8] = $"Frames: {(frameTimes != null ? frameTimes.Count : 0)}";
    }

    private void OnGUI()
    {
        // Suppressed during the measured window unless explicitly enabled, so
        // IMGUI's own per-frame allocation stays out of the GC.Alloc result.
        // The warm-up window still draws, so the run is visibly progressing.
        if (testRunning && !warmingUp && !drawHudDuringRun)
        {
            return;
        }

        if (hudLines[0] == null)
        {
            RebuildHudLines();
        }

        Color previousColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.72f);
        GUI.Box(new Rect(10f, 10f, 300f, 250f), GUIContent.none);
        GUI.color = previousColor;

        for (int i = 0; i < hudLines.Length; i++)
        {
            GUI.Label(new Rect(20f, 20f + i * 25f, 270f, 25f), hudLines[i]);
        }
    }
}
