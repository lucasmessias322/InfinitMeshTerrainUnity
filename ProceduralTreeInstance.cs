using UnityEngine;

[DisallowMultipleComponent]
public sealed class ProceduralTreeInstance : MonoBehaviour
{
    private InfinitMeshTerrain owner;
    private float maxHealth = 1f;
    private float health = 1f;

    public ulong TreeId { get; private set; }
    public Vector2Int ChunkCoord { get; private set; }
    public int PrototypeIndex { get; private set; }
    public float Health => health;
    public float MaxHealth => maxHealth;
    public bool IsInitialized => owner != null;

    public void Initialize(
        InfinitMeshTerrain terrain,
        ulong treeId,
        Vector2Int chunkCoord,
        int prototypeIndex,
        float treeMaxHealth)
    {
        owner = terrain;
        TreeId = treeId;
        ChunkCoord = chunkCoord;
        PrototypeIndex = prototypeIndex;
        maxHealth = Mathf.Max(0.01f, treeMaxHealth);
        health = maxHealth;
    }

    public void ResetRuntimeState()
    {
        owner = null;
        TreeId = 0UL;
        ChunkCoord = default;
        PrototypeIndex = -1;
        maxHealth = 1f;
        health = 1f;
    }

    public void ApplyDamage(float damage, Vector3 hitPoint, Vector3 impulse)
    {
        if (damage <= 0f || owner == null)
        {
            return;
        }

        health = Mathf.Max(0f, health - damage);
        if (health <= 0f)
        {
            DestroyTree(hitPoint, impulse);
        }
    }

    public void DestroyTree(Vector3 hitPoint, Vector3 impulse)
    {
        if (owner == null)
        {
            return;
        }

        owner.NotifyProceduralTreeDestroyed(this, hitPoint, impulse);
    }
}
