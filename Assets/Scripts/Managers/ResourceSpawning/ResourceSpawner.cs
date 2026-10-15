using System.Collections.Generic;
using PurrNet;
using UnityEngine;

namespace ProgressiveSample
{
    public class ResourceSpawner : NetworkIdentity
    {
        [Header("Layers")]
        public SpawnLayer[] layers;

        [Header("Spawn Settings")]
        public float spawnRadius = 50f;
        public float despawnRadius = 100f;
        public int maxSpawnsPerFrame = 5;
        public float spawnInterval = 0.5f;

        [Header("Density")]
        public float noiseScale = 0.05f;
        public float densityThreshold = 0.4f;
        public float minSpacing = 3f;

        [Header("Respawn Settings")]
        public float respawnDelay = 30f;

        [Header("Validation")]
        public LayerMask overlapMask;
        public int maxPlacementAttempts = 10;

        private readonly List<Transform> _players = new();
        private readonly List<Transform> _pendingBurst = new();
        private readonly List<SpawnExclusionZone> _exclusionZones = new();

        private readonly HashSet<Vector2Int> _occupiedCells = new();
        private readonly HashSet<Vector2Int> _attemptedCells = new();
        private readonly List<Vector2Int> _attemptedCellsList = new();

        private readonly Dictionary<Vector2Int, GameObject> _spawnedResources = new();
        private readonly List<Vector2Int> _spawnedResourceKeys = new();

        private readonly Dictionary<Vector2Int, float> _respawnCells = new();
        private readonly List<Vector2Int> _respawnCellKeys = new();

        private readonly List<float> _layerWeights = new();
        private readonly List<Vector2Int> _toRemoveCells = new();
        private readonly List<Vector2Int> _toCleanupCells = new();

        private Collider[] _overlapBuffer = new Collider[16];
        private float _spawnTimer;
        private float _noiseOffsetX;
        private float _noiseOffsetZ;
        private bool _initialized;

        private SpawnContext _spawnContext;

        private void Awake()
        {
            InstanceHandler.RegisterInstance(this);
        }

        protected override void OnSpawned()
        {
            base.OnSpawned();
            if (isServer)
                InitSeed(Random.Range(-10000, 10000));
        }

        [ObserversRpc(runLocally: true, bufferLast: true)]
        private void InitSeed(int seed)
        {
            Random.InitState(seed);
            _noiseOffsetX = Random.Range(0f, 10000f);
            _noiseOffsetZ = Random.Range(0f, 10000f);
            _initialized = true;

            for (int i = 0; i < _pendingBurst.Count; i++)
                BurstSpawnAroundPlayer(_pendingBurst[i]);
            _pendingBurst.Clear();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            InstanceHandler.UnregisterInstance<ResourceSpawner>();
        }

        public void RegisterPlayer(Transform player)
        {
            if (_players.Contains(player)) return;
            _players.Add(player);
            if (_initialized)
                BurstSpawnAroundPlayer(player);
            else
                _pendingBurst.Add(player);
        }

        public void UnregisterPlayer(Transform player)
        {
            _players.Remove(player);
            _pendingBurst.Remove(player);
        }

        public void RegisterExclusionZone(SpawnExclusionZone zone)
        {
            if (!_exclusionZones.Contains(zone))
                _exclusionZones.Add(zone);
        }

        public void UnregisterExclusionZone(SpawnExclusionZone zone)
        {
            _exclusionZones.Remove(zone);
        }

        public void OnResourceHarvested(GameObject resource)
        {
            Vector2Int foundCell = default;
            bool found = false;

            for (int i = 0; i < _spawnedResourceKeys.Count; i++)
            {
                if (_spawnedResources[_spawnedResourceKeys[i]] == resource)
                {
                    foundCell = _spawnedResourceKeys[i];
                    found = true;
                    break;
                }
            }

            if (!found) return;

            RemoveSpawnedResource(foundCell);
            _occupiedCells.Remove(foundCell);

            if (!_attemptedCells.Contains(foundCell))
            {
                _attemptedCells.Add(foundCell);
                _attemptedCellsList.Add(foundCell);
            }

            _respawnCells[foundCell] = Time.time + respawnDelay;
            if (!_respawnCellKeys.Contains(foundCell))
                _respawnCellKeys.Add(foundCell);
        }

        private void BurstSpawnAroundPlayer(Transform player)
        {
            Vector3 playerPos = player.position;
            int cellRadius = Mathf.CeilToInt(spawnRadius / minSpacing);
            Vector2Int playerCell = WorldToCell(playerPos);
            float spawnRadiusSqr = spawnRadius * spawnRadius;

            for (int x = -cellRadius; x <= cellRadius; x++)
            {
                for (int z = -cellRadius; z <= cellRadius; z++)
                {
                    Vector2Int cell = new Vector2Int(playerCell.x + x, playerCell.y + z);
                    if ((CellToWorld(cell) - playerPos).sqrMagnitude > spawnRadiusSqr)
                        continue;
                    TrySpawnAtCell(cell);
                }
            }
        }

        private void Update()
        {
            if (!_initialized) return;
            if (_players.Count == 0) return;

            _spawnTimer -= Time.deltaTime;
            if (_spawnTimer <= 0f)
            {
                _spawnTimer = spawnInterval;
                ProcessSpawning();
                CleanupDistantResources();
            }
        }

        private void ProcessSpawning()
        {
            float now = Time.time;
            _toRemoveCells.Clear();

            for (int i = 0; i < _respawnCellKeys.Count; i++)
            {
                var cell = _respawnCellKeys[i];
                if (_respawnCells[cell] <= now)
                    _toRemoveCells.Add(cell);
            }

            for (int i = 0; i < _toRemoveCells.Count; i++)
            {
                var cell = _toRemoveCells[i];
                _respawnCells.Remove(cell);
                _respawnCellKeys.Remove(cell);

                if (_attemptedCells.Remove(cell))
                    _attemptedCellsList.Remove(cell);
            }

            int spawned = 0;
            float spawnRadiusSqr = spawnRadius * spawnRadius;

            for (int p = 0; p < _players.Count && spawned < maxSpawnsPerFrame; p++)
            {
                Vector3 playerPos = _players[p].position;
                int cellRadius = Mathf.CeilToInt(spawnRadius / minSpacing);
                Vector2Int playerCell = WorldToCell(playerPos);

                for (int x = -cellRadius; x <= cellRadius && spawned < maxSpawnsPerFrame; x++)
                {
                    for (int z = -cellRadius; z <= cellRadius && spawned < maxSpawnsPerFrame; z++)
                    {
                        Vector2Int cell = new Vector2Int(playerCell.x + x, playerCell.y + z);
                        if ((CellToWorld(cell) - playerPos).sqrMagnitude > spawnRadiusSqr) continue;
                        if (_occupiedCells.Contains(cell)) continue;
                        if (_attemptedCells.Contains(cell)) continue;
                        if (TrySpawnAtCell(cell)) spawned++;
                    }
                }
            }
        }

        private bool TrySpawnAtCell(Vector2Int cell)
        {
            if (_occupiedCells.Contains(cell)) return false;
            if (_attemptedCells.Contains(cell)) return false;

            _attemptedCells.Add(cell);
            _attemptedCellsList.Add(cell);

            Vector3 position = CellToWorld(cell);

            float noise = Mathf.PerlinNoise(
                position.x * noiseScale + _noiseOffsetX,
                position.z * noiseScale + _noiseOffsetZ);

            if (noise < densityThreshold) return false;
            if (IsInExclusionZone(position)) return false;

            SpawnLayer layer = SelectLayer(position);
            if (layer == null) return false;
            if (!IsPositionClear(position, layer.minDistanceBetween)) return false;

            GameObject prefab = layer.GetPrefab();
            if (prefab == null) return false;

            Vector3 jitter = new Vector3(
                Random.Range(-minSpacing * 0.4f, minSpacing * 0.4f),
                0f,
                Random.Range(-minSpacing * 0.4f, minSpacing * 0.4f));

            Vector3 finalPos = position + jitter;
            GameObject instance = Instantiate(prefab, finalPos, layer.GetRotation(finalPos));
            instance.transform.localScale = layer.GetScale(finalPos);

            _occupiedCells.Add(cell);
            _spawnedResources[cell] = instance;
            _spawnedResourceKeys.Add(cell);

            return true;
        }

        private void CleanupDistantResources()
        {
            float despawnRadiusSqr = despawnRadius * despawnRadius;

            _toCleanupCells.Clear();
            for (int i = 0; i < _spawnedResourceKeys.Count; i++)
            {
                var cell = _spawnedResourceKeys[i];
                var instance = _spawnedResources[cell];

                if (!instance)
                {
                    _toCleanupCells.Add(cell);
                    continue;
                }

                Vector3 resourcePos = instance.transform.position;
                bool nearAnyPlayer = false;

                for (int p = 0; p < _players.Count; p++)
                {
                    if ((_players[p].position - resourcePos).sqrMagnitude <= despawnRadiusSqr)
                    {
                        nearAnyPlayer = true;
                        break;
                    }
                }

                if (!nearAnyPlayer)
                    _toCleanupCells.Add(cell);
            }

            for (int i = 0; i < _toCleanupCells.Count; i++)
            {
                var cell = _toCleanupCells[i];
                if (_spawnedResources.TryGetValue(cell, out var instance) && instance)
                    Destroy(instance);

                RemoveSpawnedResource(cell);
                _occupiedCells.Remove(cell);

                if (_attemptedCells.Remove(cell))
                    _attemptedCellsList.Remove(cell);
            }

            _toCleanupCells.Clear();
            for (int i = 0; i < _attemptedCellsList.Count; i++)
            {
                var cell = _attemptedCellsList[i];
                if (_respawnCells.ContainsKey(cell)) continue;

                Vector3 cellWorld = CellToWorld(cell);
                bool nearAny = false;

                for (int p = 0; p < _players.Count; p++)
                {
                    if ((_players[p].position - cellWorld).sqrMagnitude <= despawnRadiusSqr)
                    {
                        nearAny = true;
                        break;
                    }
                }

                if (!nearAny)
                    _toCleanupCells.Add(cell);
            }

            for (int i = 0; i < _toCleanupCells.Count; i++)
            {
                var cell = _toCleanupCells[i];
                if (_attemptedCells.Remove(cell))
                    _attemptedCellsList.Remove(cell);
            }
        }

        private void RemoveSpawnedResource(Vector2Int cell)
        {
            _spawnedResources.Remove(cell);
            _spawnedResourceKeys.Remove(cell);
        }

        private SpawnLayer SelectLayer(Vector3 position)
        {
            float distanceFromCenter = new Vector2(position.x, position.z).magnitude;
            _spawnContext.Position = position;
            _spawnContext.DistanceFromCenter = distanceFromCenter;
            _spawnContext.GameTime = Time.time;

            _layerWeights.Clear();
            float totalWeight = 0f;

            for (int i = 0; i < layers.Length; i++)
            {
                float weight = layers[i].GetWeight(_spawnContext);
                _layerWeights.Add(weight);
                totalWeight += weight;
            }

            if (totalWeight <= 0f) return null;

            float random = Random.value * totalWeight;
            float cumulative = 0f;

            for (int i = 0; i < layers.Length; i++)
            {
                cumulative += _layerWeights[i];
                if (random <= cumulative)
                    return layers[i];
            }

            return layers[^1];
        }

        private bool IsInExclusionZone(Vector3 position)
        {
            for (int i = 0; i < _exclusionZones.Count; i++)
            {
                var zone = _exclusionZones[i];
                if (!zone) continue;
                if ((zone.transform.position - position).sqrMagnitude <= zone.radius * zone.radius)
                    return true;
            }
            return false;
        }

        private bool IsPositionClear(Vector3 position, float minDistance)
        {
            return Physics.OverlapSphereNonAlloc(position, minDistance, _overlapBuffer, overlapMask) == 0;
        }

        private Vector2Int WorldToCell(Vector3 position)
        {
            return new Vector2Int(
                Mathf.FloorToInt(position.x / minSpacing),
                Mathf.FloorToInt(position.z / minSpacing));
        }

        private Vector3 CellToWorld(Vector2Int cell)
        {
            return new Vector3(
                (cell.x + 0.5f) * minSpacing,
                0f,
                (cell.y + 0.5f) * minSpacing);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(transform.position, spawnRadius);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, despawnRadius);
        }
    }
}