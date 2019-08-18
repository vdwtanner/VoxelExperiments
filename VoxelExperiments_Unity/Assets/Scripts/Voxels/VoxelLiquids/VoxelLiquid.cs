﻿using System.Collections.Generic;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;

using Profiler = UnityEngine.Profiling.Profiler;

[RequireComponent(typeof(MeshFilter))]
public class VoxelLiquid : MonoBehaviour
{
	const int INITIAL_VOXEL_CAPACITY = 256;
	const float CAPACITY_SCALING_FACTOR = 1.5f;

	public bool rain = false;

	public float propogationDelay = 1.0f;
	public float timeToSettle = 5.0f;
	public GameObject[] voxelPrefabs = new GameObject[7];

	private Dictionary<Vector3, LiquidVoxel> waterVoxels;
	private Dictionary<Vector3, LiquidVoxel> writeVoxels;
	private float lastPropogationTime;
	private float lastUnsettledTime;

	private MeshFilter meshFilter;
	private Mesh mesh;

	private List<GameObject> voxelGobs;

	private bool meshUpdated = false;
	public bool IsSettled { get; set; }

	#region meshingJob fields
	private JobHandle meshingJobHandle;
	private NativeArray<float> heightLookup;

	private NativeArray<MeshableVoxel> meshableVoxels;
	private NativeArray<Vector3> verts;
	private NativeArray<Vector3> normals;
	private NativeArray<int> tris;
	private NativeArray<int> counts;
	#endregion

	private void Awake()
	{
		heightLookup = new NativeArray<float>(8, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
		heightLookup[0] = 0;
		heightLookup[1] = 0.1428571f;
		heightLookup[2] = 0.2857143f;
		heightLookup[3] = 0.4285714f;
		heightLookup[4] = 0.5714286f;
		heightLookup[5] = 0.7142857f;
		heightLookup[6] = 0.8571429f;
		heightLookup[7] = 1f;

		counts = new NativeArray<int>(4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

		meshableVoxels = new NativeArray<MeshableVoxel>(INITIAL_VOXEL_CAPACITY, Allocator.Persistent);
		verts = new NativeArray<Vector3>(INITIAL_VOXEL_CAPACITY * 24, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
		normals = new NativeArray<Vector3>(INITIAL_VOXEL_CAPACITY * 24, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
		tris = new NativeArray<int>(INITIAL_VOXEL_CAPACITY * 36, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
	}

	// Start is called before the first frame update
	void Start()
	{
		voxelGobs = new List<GameObject>();
		waterVoxels = new Dictionary<Vector3, LiquidVoxel>();
		writeVoxels = new Dictionary<Vector3, LiquidVoxel>();

		waterVoxels.Add(new Vector3(0, 1, 0), new LiquidVoxel(1, 7));
		/*waterVoxels.Add(new Vector3(0, 3, 0), new LiquidVoxel(1, 2));
		waterVoxels.Add(new Vector3(0, 5, 0), new LiquidVoxel(1, 2));
		waterVoxels.Add(new Vector3(0, 7, 0), new LiquidVoxel(1, 4));
		waterVoxels.Add(new Vector3(0, 9, 0), new LiquidVoxel(1, 1));

		waterVoxels.Add(new Vector3(2, 9, 1), new LiquidVoxel(1, 7));*/

		lastPropogationTime = Time.time;
		lastUnsettledTime = Time.time;

		meshFilter = gameObject.GetComponent<MeshFilter>();
		mesh = new Mesh();
		meshFilter.mesh = mesh;
		mesh.MarkDynamic();

		Remesh();
	}

	// Update is called once per frame
	void Update()
	{
		if (meshingJobHandle.IsCompleted && !meshUpdated)
		{
			UpdateMesh();
			return;
		}

		if (meshingJobHandle.IsCompleted
			&& !IsSettled
			&& Time.time - lastPropogationTime >= propogationDelay)
		{
			Propagate();
			lastPropogationTime = Time.time;
		}
	}

	private void OnDestroy()
	{
		meshingJobHandle.Complete();

		heightLookup.Dispose();
		meshableVoxels.Dispose();
		verts.Dispose();
		normals.Dispose();
		tris.Dispose();
		counts.Dispose();
	}

	private void Propagate()
	{
		Profiler.BeginSample("Propagate()");
		bool changed = false;
		if (rain && Random.Range(0f, 1f) > .1f)
		{
			Vector3 pos = new Vector3(Random.Range(0, 10), 8, Random.Range(0, 10));
			if (!waterVoxels.ContainsKey(pos))
			{
				waterVoxels.Add(pos, new LiquidVoxel(1, 7));
				changed = true;
			}
		}

		writeVoxels.Clear();
		foreach (var tuple in waterVoxels)
		{
			Vector3 pos = tuple.Key;
			LiquidVoxel voxel = tuple.Value;
			if (TryPropagateDown(pos, voxel))
			{
				changed = true;
				continue;
			}
			else if (TryPropagateOut(pos, voxel))
			{
				changed = true;
				continue;
			}
			else
			{
				SetWriteVoxel(pos, GetVoxelAt(pos));
			}
		}

		Dictionary<Vector3, LiquidVoxel> temp = waterVoxels;
		waterVoxels = writeVoxels;
		writeVoxels = temp;
		if (changed)
		{
			lastUnsettledTime = Time.time;
			Remesh();
		}
		else if (Time.time - lastUnsettledTime > timeToSettle)
		{
			IsSettled = true;
		}
		Profiler.EndSample();
	}

	public void Remesh()
	{
		Profiler.BeginSample("Remesh()");
		meshUpdated = false;

		if (waterVoxels.Count > meshableVoxels.Length)
		{
			//Scale up to meet the new need
			DisposeOfSizeDependentJobCollections();
			int newCapacity = (int)(waterVoxels.Count * CAPACITY_SCALING_FACTOR);
			Debug.LogWarning($"Scaling up VoxelLiquid capacity! New capacity is {newCapacity} voxels.");

			meshableVoxels = new NativeArray<MeshableVoxel>(newCapacity, Allocator.Persistent);
			verts = new NativeArray<Vector3>(newCapacity * 24, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			normals = new NativeArray<Vector3>(newCapacity * 24, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			tris = new NativeArray<int>(newCapacity * 36, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
		}


		Profiler.BeginSample("Copy voxels");
		BuildMeshableVoxels();
		counts[0] = waterVoxels.Count;
		Profiler.EndSample();

		VoxelLiquidMeshingJob meshingJob = new VoxelLiquidMeshingJob
		{
			heightLookup = heightLookup,
			voxels = meshableVoxels,
			verts = verts,
			tris = tris,
			normals = normals,
			counts = counts
		};

		meshingJobHandle = meshingJob.Schedule();
		Profiler.EndSample();
	}

	private void UpdateMesh()
	{
		meshingJobHandle.Complete();

		int vertCount = counts[1];
		int triCount = counts[2];

		mesh.Clear();
		mesh.vertices = verts.Slice(0, vertCount).ToArray();
		mesh.triangles = tris.Slice(0, triCount).ToArray();
		mesh.normals = normals.Slice(0, counts[3]).ToArray();
		meshUpdated = true;
	}

	private void DisposeOfSizeDependentJobCollections()
	{
		meshingJobHandle.Complete();

		meshableVoxels.Dispose();
		verts.Dispose();
		normals.Dispose();
		tris.Dispose();
	}

	private bool TryPropagateDown(Vector3 pos, LiquidVoxel voxel)
	{
		Vector3 checkPos = pos + Vector3.down;
		LiquidVoxel lowerVoxel = GetVoxelAt(checkPos);

		//Can't propagate to solid
		if (lowerVoxel.IsSolid)
			return false;

		if (lowerVoxel.IsAir)
		{
			//Just move this voxel down one
			SetWriteVoxel(checkPos, voxel);
			return true;
		}

		//TODO: Add support for combining VoxelLiquids
		int capacity = LiquidVoxel.MAX_VOLUME - lowerVoxel.Volume;
		if (capacity > 0)
		{
			byte amount = (byte)Mathf.Min(capacity, voxel.Volume);
			SetWriteVoxel(checkPos, lowerVoxel.AddVolume(amount));
			if (voxel.Volume > amount)
			{
				SetWriteVoxel(pos, voxel.LessVolume(amount));
			}
			return true;
		}
		return false;
	}

	private bool TryPropagateOut(Vector3 pos, LiquidVoxel voxel)
	{
		//Can only propagate if there is more than 1 unit of volume
		if (voxel.Volume <= 1)
			return false;

		Vector3[] surroundingPositions = new Vector3[]
		{
			pos - Vector3.right,	//left
			pos + Vector3.right,	//right
			pos + Vector3.forward,	//front
			pos - Vector3.forward	//rear
		};

		bool[] updated = new bool[] { false, false, false, false };

		LiquidVoxel[] surroundingVoxels = new LiquidVoxel[4];
		for (int i = 0; i < 4; i++)
		{
			surroundingVoxels[i] = GetVoxelAt(surroundingPositions[i]);
		}

		//First try to propagate to air spaces
		int numAirSpaces = 0;
		foreach (LiquidVoxel vox in surroundingVoxels)
		{
			if (vox.IsAir)
			{
				numAirSpaces++;
			}
		}

		//distribute to nearby cells
		int minimumVolume = 7;
		for (int i = 0; i < 4; i++)
		{
			if (surroundingVoxels[i].Volume < minimumVolume)
				minimumVolume = surroundingVoxels[i].Volume;
		}

		bool propagated = false;
		int currentVolume = voxel.Volume;
		int randomOffset = Random.Range(0, 3);
		while (currentVolume > minimumVolume)
		{
			//distribute to minimum volume voxels
			for (int i = 0; i < 4; i++)
			{
				int index = (i + randomOffset) % 4;
				if ((currentVolume) <= minimumVolume || currentVolume == 1)
					break;

				//Can't propagate to a solid
				if (surroundingVoxels[index].IsSolid)
					continue;

				if (surroundingVoxels[index].Volume == minimumVolume)
				{
					if (surroundingVoxels[index].IsAir)
						surroundingVoxels[index] = new LiquidVoxel(voxel.Id, 1);
					else
						surroundingVoxels[index] = surroundingVoxels[index].AddVolume(1);
					updated[index] = true;
					currentVolume--;
					propagated = true;
				}
			}
			minimumVolume++;
		}

		if (propagated)
		{
			for (int i = 0; i < 4; i++)
			{
				if (surroundingVoxels[i].IsSolid)
					continue;

				if (surroundingVoxels[i].Volume > 0 && updated[i])
				{
					SetWriteVoxel(surroundingPositions[i], surroundingVoxels[i]);
				}
			}
			SetWriteVoxel(pos, voxel.WithVolume(currentVolume));
		}



		return propagated;
	}

	private void SetWriteVoxel(Vector3 pos, LiquidVoxel voxel)
	{
		if (writeVoxels.ContainsKey(pos))
			writeVoxels[pos] = voxel;
		else
			writeVoxels.Add(pos, voxel);
	}

	private LiquidVoxel GetVoxelAt(Vector3 pos)
	{
		if (writeVoxels.TryGetValue(pos, out LiquidVoxel newVoxel))
			return newVoxel;

		if (waterVoxels.TryGetValue(pos, out LiquidVoxel existingVoxel))
			return existingVoxel;

		return AskWorldForVoxel(pos);
	}

	private LiquidVoxel GetVoxelAtNoWriteVoxels(Vector3 pos)
	{
		if (waterVoxels.TryGetValue(pos, out LiquidVoxel existingVoxel))
			return existingVoxel;

		return AskWorldForVoxel(pos);
	}

	LiquidVoxel AskWorldForVoxel(Vector3 location)
	{
		if (location.y <= 0)
		{
			return new LiquidVoxel(-1); //stone
		}
		else return LiquidVoxel.AIR;
	}

	private void BuildMeshableVoxels()
	{
		int i = 0;
		foreach (var tuple in waterVoxels)
		{
			Vector3 pos = tuple.Key;
			LiquidVoxel vox = tuple.Value;
			meshableVoxels[i++] = new MeshableVoxel
			{
				position = pos,
				id = vox.Id,
				Volume = vox.Volume,
				upVolume = GetVoxelAtNoWriteVoxels(pos + Vector3.up).Volume,
				downVolume = GetVoxelAtNoWriteVoxels(pos + Vector3.down).Volume,
				leftVolume = GetVoxelAtNoWriteVoxels(pos + Vector3.left).Volume,
				rightVolume = GetVoxelAtNoWriteVoxels(pos + Vector3.right).Volume,
				frontVolume = GetVoxelAtNoWriteVoxels(pos + Vector3.forward).Volume,
				backVolume = GetVoxelAtNoWriteVoxels(pos + Vector3.back).Volume
			};
		}
	}
}


public struct LiquidVoxel
{
	public int Id;
	public int Volume;
	public bool IsLiquid => Id > 0;
	public bool IsAir => Id == 0;
	public bool IsSolid => Id < 0;
	public static int MAX_VOLUME = 7;

	public static LiquidVoxel AIR => new LiquidVoxel(0, 0);

	public LiquidVoxel(int id, int volume = 7)
	{
		Id = id;
		Volume = Mathf.Clamp(volume, 0, 7);
	}

	public LiquidVoxel WithVolume(int volume)
	{
		return new LiquidVoxel(Id, volume);
	}

	public LiquidVoxel LessVolume(int volumeToSubtract)
	{
		return new LiquidVoxel(Id, Volume - volumeToSubtract);
	}

	public LiquidVoxel AddVolume(int extraVolume)
	{
		return new LiquidVoxel(Id, Volume + extraVolume);
	}
}

public struct VoxelLiquidMeshingJob : IJob
{
	/*	   c_______d
	 *	   /|     /|
	 *	 b/_|____/a|		y
	 *	  |g|____|_|h		|  z
	 *    | /    | /		| /
	 *    |/_____|/			|/_____x
	 *    f      e
	 *    
	 *    f is 0,0,0 from pos of Voxel
	 */


	[ReadOnly] public NativeArray<float> heightLookup;
	[ReadOnly] public NativeArray<MeshableVoxel> voxels;

	[WriteOnly] public NativeArray<Vector3> verts;
	[WriteOnly] public NativeArray<Vector3> normals;
	[WriteOnly] public NativeArray<int> tris;


	public NativeArray<int> counts;

	public void Execute()
	{

		counts[1] = 0;
		counts[2] = 0;
		counts[3] = 0;
		for (int i = 0; i < counts[0]; i++)
		{

			MeshableVoxel vox = voxels[i];
			MeshTop(vox);
			MeshLeft(vox);
			MeshRight(vox);
			MeshFront(vox);
			MeshBack(vox);
			MeshBottom(vox);
		}
	}

	private void MeshTop(MeshableVoxel voxel)
	{
		if (voxel.ShouldMeshTop)
		{
			Vector3 pos = voxel.position;
			Vector3 a = new Vector3(pos.x + 1, pos.y + heightLookup[voxel.Volume], pos.z);
			Vector3 b = new Vector3(pos.x, pos.y + heightLookup[voxel.Volume], pos.z);
			Vector3 c = new Vector3(pos.x, pos.y + heightLookup[voxel.Volume], pos.z + 1);
			Vector3 d = new Vector3(pos.x + 1, pos.y + heightLookup[voxel.Volume], pos.z + 1);

			AddPointsAndTriangulate(a, b, c, d, Vector3.up);
		}
	}

	private void MeshBottom(MeshableVoxel voxel)
	{
		if (voxel.ShouldMeshBottom)
		{
			Vector3 pos = voxel.position;
			Vector3 e = new Vector3(pos.x + 1, pos.y, pos.z);
			Vector3 f = new Vector3(pos.x, pos.y, pos.z);
			Vector3 g = new Vector3(pos.x, pos.y, pos.z + 1);
			Vector3 h = new Vector3(pos.x + 1, pos.y, pos.z + 1);

			AddPointsAndTriangulate(g, f, e, h, Vector3.down);
		}
	}


	private void MeshLeft(MeshableVoxel voxel)
	{
		if (voxel.ShouldMeshLeft)
		{
			Vector3 pos = voxel.position;
			Vector3 b = new Vector3(pos.x, pos.y + heightLookup[voxel.Volume], pos.z);
			Vector3 c = new Vector3(pos.x, pos.y + heightLookup[voxel.Volume], pos.z + 1);
			Vector3 g = new Vector3(pos.x, pos.y, pos.z + 1);
			Vector3 f = new Vector3(pos.x, pos.y, pos.z);

			AddPointsAndTriangulate(g, c, b, f, Vector3.left);
		}
	}

	private void MeshRight(MeshableVoxel voxel)
	{
		if (voxel.ShouldMeshRight)
		{
			Vector3 pos = voxel.position;

			Vector3 a = new Vector3(pos.x + 1, pos.y + heightLookup[voxel.Volume], pos.z);
			Vector3 d = new Vector3(pos.x + 1, pos.y + heightLookup[voxel.Volume], pos.z + 1);
			Vector3 h = new Vector3(pos.x + 1, pos.y, pos.z + 1);
			Vector3 e = new Vector3(pos.x + 1, pos.y, pos.z);

			AddPointsAndTriangulate(a, d, h, e, Vector3.right);
		}
	}

	private void MeshFront(MeshableVoxel voxel)
	{
		if (voxel.ShouldMeshFront)
		{
			Vector3 pos = voxel.position;
			Vector3 c = new Vector3(pos.x, pos.y + heightLookup[voxel.Volume], pos.z + 1);
			Vector3 d = new Vector3(pos.x + 1, pos.y + heightLookup[voxel.Volume], pos.z + 1);
			Vector3 h = new Vector3(pos.x + 1, pos.y, pos.z + 1);
			Vector3 g = new Vector3(pos.x, pos.y, pos.z + 1);

			AddPointsAndTriangulate(d, c, g, h, Vector3.forward);
		}
	}

	private void MeshBack(MeshableVoxel voxel)
	{
		if (voxel.ShouldMeshBack)
		{
			Vector3 pos = voxel.position;

			Vector3 b = new Vector3(pos.x, pos.y + heightLookup[voxel.Volume], pos.z);
			Vector3 a = new Vector3(pos.x + 1, pos.y + heightLookup[voxel.Volume], pos.z);
			Vector3 e = new Vector3(pos.x + 1, pos.y, pos.z);
			Vector3 f = new Vector3(pos.x, pos.y, pos.z);

			AddPointsAndTriangulate(b, a, e, f, Vector3.back);
		}
	}

	private void AddPointsAndTriangulate(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, Vector3 normal)
	{
		int vertIndex = counts[1];
		verts[counts[1]++] = p1;
		verts[counts[1]++] = p2;
		verts[counts[1]++] = p3;
		verts[counts[1]++] = p4;

		//Tri 1
		tris[counts[2]++] = vertIndex;
		tris[counts[2]++] = vertIndex + 1;
		tris[counts[2]++] = vertIndex + 2;

		//Tri 2
		tris[counts[2]++] = vertIndex;
		tris[counts[2]++] = vertIndex + 2;
		tris[counts[2]++] = vertIndex + 3;

		//All normals face the same direction
		normals[counts[3]++] = normal;
		normals[counts[3]++] = normal;
		normals[counts[3]++] = normal;
		normals[counts[3]++] = normal;
	}
}

public struct MeshableVoxel
{
	public Vector3 position;
	public int id;
	public int Volume;

	public int upVolume;
	public int downVolume;
	public int leftVolume;
	public int rightVolume;
	public int frontVolume;
	public int backVolume;

	public bool ShouldMeshTop => Volume < 7 || upVolume == 0;
	public bool ShouldMeshLeft => Volume > leftVolume;
	public bool ShouldMeshRight => Volume > rightVolume;
	public bool ShouldMeshFront => Volume > frontVolume;
	public bool ShouldMeshBack => Volume > backVolume;
	public bool ShouldMeshBottom => downVolume < 7;
}