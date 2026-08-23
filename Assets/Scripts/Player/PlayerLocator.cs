using UnityEngine;

/// <summary>
/// Single cached lookup point for the player Transform.
///
/// Enemy scripts previously resolved the player inside OnEnable(), which fires
/// on every spawn *and* on every pool reuse. Each of those calls was a
/// full-scene scan (FindAnyObjectByType / FindGameObjectWithTag) whose cost
/// grows with the number of objects in the scene, and it was paid identically
/// whether pooling was on or off. That constant per-spawn cost dwarfed the
/// Instantiate() call pooling exists to remove, which is why a pooled run and
/// an unpooled run measured the same.
///
/// The lookup now happens once and is reused. Unity's overloaded == operator
/// reports a destroyed object as null, so the cache invalidates itself
/// automatically when the player is destroyed or a new scene is loaded.
/// </summary>
public static class PlayerLocator
{
    private static Transform cachedPlayer;

    /// <summary>
    /// The current player Transform, or null when no player exists.
    /// Resolves lazily on first use and on the first access after the previous
    /// player was destroyed.
    /// </summary>
    public static Transform Player
    {
        get
        {
            if (cachedPlayer != null)
            {
                return cachedPlayer;
            }

            Resolve();
            return cachedPlayer;
        }
    }

    /// <summary>
    /// Lets the player itself publish its Transform (e.g. from Awake) so the
    /// very first enemy spawn does not have to pay for a scene scan at all.
    /// </summary>
    public static void Register(Transform playerTransform)
    {
        cachedPlayer = playerTransform;
    }

    public static void Clear()
    {
        cachedPlayer = null;
    }

    private static void Resolve()
    {
        GameObject taggedPlayer = GameObject.FindGameObjectWithTag("Player");
        if (taggedPlayer != null)
        {
            cachedPlayer = taggedPlayer.transform;
            return;
        }

        // Fallback for scenes where the Player tag has not been applied.
        PlayerMovement playerMovement = Object.FindAnyObjectByType<PlayerMovement>();
        cachedPlayer = playerMovement != null ? playerMovement.transform : null;
    }

    /// <summary>
    /// Static state survives entering Play Mode when Domain Reload is disabled,
    /// so clear it explicitly at subsystem registration. Without this a stale
    /// reference from a previous Play session could leak into the next one and
    /// make two performance-test runs start from different states.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        cachedPlayer = null;
    }
}
