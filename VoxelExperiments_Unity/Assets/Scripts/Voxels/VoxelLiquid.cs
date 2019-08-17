using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VoxelLiquid : MonoBehaviour
{
	public float propogationDelay = 1.0f;
	public GameObject[] voxelPrefabs = new GameObject[7];

	private Dictionary<Vector3, LiquidVoxel> waterVoxels;
	private Dictionary<Vector3, LiquidVoxel> writeVoxels;
	private float lastPropogationTime;

	List<GameObject> voxelGobs;

    // Start is called before the first frame update
    void Start()
    {
		voxelGobs = new List<GameObject>();
		waterVoxels = new Dictionary<Vector3, LiquidVoxel>();
		writeVoxels = new Dictionary<Vector3, LiquidVoxel>();

		waterVoxels.Add(new Vector3(0, 1, 0), new LiquidVoxel(1, 7));
		waterVoxels.Add(new Vector3(0, 3, 0), new LiquidVoxel(1, 2));
		waterVoxels.Add(new Vector3(0, 5, 0), new LiquidVoxel(1, 2));
		waterVoxels.Add(new Vector3(0, 7, 0), new LiquidVoxel(1, 4));
		waterVoxels.Add(new Vector3(0, 9, 0), new LiquidVoxel(1, 1));

		waterVoxels.Add(new Vector3(2, 9, 1), new LiquidVoxel(1, 7));

		lastPropogationTime = Time.time;
		Remesh();
    }

    // Update is called once per frame
    void Update()
    {
        if(Time.time - lastPropogationTime >= propogationDelay)
		{
			Propagate();
			lastPropogationTime = Time.time;
		}
    }

	private void Propagate()
	{
		bool changed = false;
		if (Random.Range(0f, 1f) > .5f)
		{
			waterVoxels.Add(new Vector3(Random.Range(0, 10), 8, Random.Range(0, 10)), new LiquidVoxel(1, 7));
			changed = true;
		}

		writeVoxels.Clear();
		foreach(var tuple in waterVoxels)
		{
			Vector3 pos = tuple.Key;
			LiquidVoxel voxel = tuple.Value;
			if(TryPropagateDown(pos, voxel))
			{
				changed = true;
				continue;
			}
			else if(TryPropagateOut(pos, voxel))
			{
				changed = true;
				continue;
			}
			else
			{
				AddOrUpdateVoxel(pos, voxel);
			}
		}

		Dictionary<Vector3, LiquidVoxel> temp = waterVoxels;
		waterVoxels = writeVoxels;
		writeVoxels = temp;
		if (changed)
		{
			Remesh();
		}
	}

	public void Remesh()
	{
		foreach(GameObject gob in voxelGobs)
		{
			Destroy(gob);
		}
		voxelGobs.Clear();

		//List<Vector3> verts = new List<Vector3>();
		//List<int> tris = new List<int>();

		foreach (var tuple in waterVoxels)
		{
			voxelGobs.Add(GameObject.Instantiate(voxelPrefabs[tuple.Value.Volume-1], tuple.Key, Quaternion.identity));
		}
	}



	public void Settle()
	{

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
		for(int i=0; i < 4; i++)
		{
			surroundingVoxels[i] = GetVoxelAt(surroundingPositions[i]);
		}

		//First try to propagate to air spaces
		int numAirSpaces = 0;
		foreach(LiquidVoxel vox in surroundingVoxels)
		{
			if (vox.IsAir)
			{
				numAirSpaces++;
			}
		}

		//distribute to nearby cells
		int minimumVolume = 7;
		for(int i = 0; i < 4; i++)
		{
			if (surroundingVoxels[i].Volume < minimumVolume)
				minimumVolume = surroundingVoxels[i].Volume;
		}

		bool propagated = false;
		int currentVolume = voxel.Volume;
		while(currentVolume > minimumVolume)
		{
			//distribute to minimum volume voxels
			for(int i = 0; i < 4; i++)
			{
				if ((currentVolume-1) <= minimumVolume || currentVolume == 1)
					break;

				//Can't propagate to a solid
				if (surroundingVoxels[i].IsSolid)
					continue;

				if (surroundingVoxels[i].Volume == minimumVolume)
				{
					if (surroundingVoxels[i].IsAir)
						surroundingVoxels[i] = new LiquidVoxel(voxel.Id, 1);
					else
						surroundingVoxels[i] = surroundingVoxels[i].AddVolume(1);
					updated[i] = true;
					currentVolume--;
					propagated = true;
				}
			}
			minimumVolume++;
		}

		if (propagated)
		{
			for(int i=0; i < 4; i++)
			{
				if (surroundingVoxels[i].IsSolid)
					continue;
				
				if(surroundingVoxels[i].Volume > 0 && updated[i])
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

	private void AddOrUpdateVoxel(Vector3 pos, LiquidVoxel voxel)
	{
		if (writeVoxels.TryGetValue(pos, out LiquidVoxel other))
		{
			writeVoxels[pos] = other.AddVolume(voxel.Volume);
		}
		else
		{
			writeVoxels.Add(pos, voxel);
		}
	}

	private LiquidVoxel GetVoxelAt(Vector3 pos)
	{
		if (writeVoxels.TryGetValue(pos, out LiquidVoxel newVoxel))
			return newVoxel;

		if (waterVoxels.TryGetValue(pos, out LiquidVoxel existingVoxel))
			return existingVoxel;

		return AskWorldForVoxel(pos);
	}

	LiquidVoxel AskWorldForVoxel(Vector3 location)
	{
		if (location.y <= 0)
		{
			return new LiquidVoxel(-1);	//stone
		}
		else return LiquidVoxel.AIR;
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
		return new LiquidVoxel(Id, Volume+extraVolume);
	}
}