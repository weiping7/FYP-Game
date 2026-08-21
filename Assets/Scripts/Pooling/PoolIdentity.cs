using UnityEngine;

public class PoolIdentity : MonoBehaviour
{
    // Set when the object came from ObjectPoolManager.GetObject(prefab, ...).
    public GameObject prefab;

    // Set when the object came from ObjectPoolManager.GetPooledObject(key, ...)
    // instead — used by procedurally-built objects that have no source prefab
    // (e.g. RuntimeWeaponProjectile, WeaponVisualEffect).
    public string poolKey;

    // Cached once when the object is first created by the pool, instead of
    // calling GetComponent<IPoolable>() on every single Get()/Release(). A
    // GetComponent<T>() call for an interface type is measurably slower than
    // for a concrete type (Unity can't use the fast native type-id lookup and
    // falls back to scanning components with an `is` check), so on a hot path
    // like object pooling — which exists specifically to cut per-spawn cost —
    // paying that tax twice per object lifecycle works against the whole point.
    [System.NonSerialized] public IPoolable poolable;
}
