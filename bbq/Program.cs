using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

class Program
{
	// 서버 실행 여부를 결정하는 플래그
	private static bool isRunning = true;

	// 연결된 클라이언트를 관리하는 스레드-안전한 딕셔너리
	private static readonly ConcurrentDictionary<uint, TcpClient> clients = new ConcurrentDictionary<uint, TcpClient>();

	// 클라이언트 ID를 생성하기 위한 카운터
	private static uint clientIdCounter = 0;

	// 각 씬의 엔티티 상태를 관리하는 스레드-안전한 딕셔너리
	private static readonly ConcurrentDictionary<string, SceneData> scenes = new ConcurrentDictionary<string, SceneData>();

	// 씬 할당을 위한 라운드 로빈 방식의 카운터
	private static int sceneAssignCounter = 0;

	// 게임 씬의 가로, 세로 크기
	private const float SceneWidth = 8f;
	private const float SceneHeight = 7f;

	/// <summary>
	/// 프로그램의 진입점으로, 서버를 초기화하고 클라이언트 연결을 수락합니다.
	/// </summary>
	static async Task Main(string[] args)
	{
		// 초기 씬을 생성합니다.
		InitializeScenes();

		// TCP 리스너를 생성하고 시작합니다.
		TcpListener listener = new TcpListener(IPAddress.Any, 3100);
		listener.Start();
		Console.WriteLine("서버가 포트 3100에서 시작되었습니다.");

		// 게임 루프를 비동기로 실행합니다.
		_ = Task.Run(GameLoop);

		// 서버가 실행 중인 동안 클라이언트 연결을 수락합니다.
		while (isRunning)
		{
			try
			{
				// 새로운 클라이언트 연결을 비동기로 수락합니다.
				TcpClient client = await listener.AcceptTcpClientAsync();

				// 클라이언트 ID를 생성합니다.
				uint clientId = ++clientIdCounter;

				// 클라이언트를 딕셔너리에 추가합니다.
				if (clients.TryAdd(clientId, client))
				{
					Console.WriteLine($"클라이언트 #{clientId} 연결됨.");

					// 클라이언트에게 할당할 씬을 결정합니다.
					string assignedSceneName = AssignScene();
					SceneData scene = scenes[assignedSceneName];

					// 새로운 플레이어 데이터를 생성하고 씬에 추가합니다.
					PlayerData player = new PlayerData
					{
						NetworkId = clientId,
						X = 0,
						Y = 0,
						SceneName = assignedSceneName,
						Name = $"Player_{clientId}"
					};
					scene.Players.TryAdd(clientId, player);
					Console.WriteLine($"{player.Name}이(가) '{assignedSceneName}'에 할당되었습니다.");

					// 클라이언트 통신을 비동기로 처리합니다.
					_ = HandleClientAsync(client, clientId);
				}
				else
				{
					Console.WriteLine($"클라이언트 #{clientId} 추가 실패.");
					client.Close();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"클라이언트 수락 중 오류 발생: {ex.Message}");
			}
		}

		// 리스너를 중지합니다.
		listener.Stop();
	}

	/// <summary>
	/// 초기 씬을 생성하고 기본 엔티티를 배치합니다.
	/// </summary>
	private static void InitializeScenes()
	{
		// 씬을 생성하고 딕셔너리에 추가합니다.
		scenes.TryAdd("Scene_Gameplay_1", new SceneData());
		scenes.TryAdd("Scene_Gameplay_2", new SceneData());

		// 각 씬에 별 엔티티를 생성합니다.
		for (int i = 0; i < 10; i++)
		{
			CreateStar("Scene_Gameplay_1");
			CreateStar("Scene_Gameplay_2");
		}

		// 첫 번째 씬에 NPC를 생성합니다.
		CreateNPC("Scene_Gameplay_1", 0f, 4f);

		Console.WriteLine("기본 씬 'Scene_Gameplay_1'과 'Scene_Gameplay_2'가 초기화되었습니다.");
	}

	/// <summary>
	/// 라운드 로빈 방식을 이용하여 클라이언트에게 씬을 할당합니다.
	/// </summary>
	/// <returns>할당된 씬의 이름</returns>
	private static string AssignScene()
	{
		var sceneNames = new List<string>(scenes.Keys);
		string assignedScene = sceneNames[sceneAssignCounter % sceneNames.Count];
		sceneAssignCounter++;
		return assignedScene;
	}

	/// <summary>
	/// 특정 씬에 별 엔티티를 생성합니다.
	/// </summary>
	/// <param name="sceneName">별을 생성할 씬의 이름</param>
	private static void CreateStar(string sceneName)
	{
		// 고유한 별 ID를 생성합니다.
		uint starId = GenerateUniqueId();

		// 별 데이터를 생성하고 위치를 랜덤하게 설정합니다.
		StarData star = new StarData
		{
			NetworkId = starId,
			X = new Random().NextSingle() * (2f * SceneWidth) - SceneWidth,
			Y = new Random().NextSingle() * (2f * SceneHeight) - SceneHeight,
			SceneName = sceneName
		};

		// 씬에 별을 추가합니다.
		scenes[sceneName].Stars.TryAdd(starId, star);
	}

	/// <summary>
	/// 특정 위치에 NPC를 생성합니다.
	/// </summary>
	/// <param name="sceneName">NPC를 생성할 씬의 이름</param>
	/// <param name="x">NPC의 X 좌표</param>
	/// <param name="y">NPC의 Y 좌표</param>
	private static void CreateNPC(string sceneName, float x, float y)
	{
		// 고유한 NPC ID를 생성합니다.
		uint npcId = GenerateUniqueId();

		// NPC 데이터를 생성합니다.
		NpcData npc = new NpcData
		{
			NetworkId = npcId,
			X = x,
			Y = y,
			SceneName = sceneName
		};

		// 씬에 NPC를 추가합니다.
		scenes[sceneName].Npcs.TryAdd(npcId, npc);
	}

	/// <summary>
	/// 클라이언트와의 통신을 처리합니다.
	/// </summary>
	/// <param name="client">클라이언트 TCP 연결</param>
	/// <param name="clientId">클라이언트의 고유 ID</param>
	private static async Task HandleClientAsync(TcpClient client, uint clientId)
	{
		// 클라이언트 통신의 타임아웃 설정 (10초)
		client.ReceiveTimeout = 10000; // 수신 타임아웃
		client.SendTimeout = 10000;    // 송신 타임아웃

		StringBuilder messageBuilder = new StringBuilder();

		try
		{
			using NetworkStream stream = client.GetStream();
			byte[] buffer = new byte[1024];
			int byteCount;

			// 클라이언트에게 할당된 ID를 전송합니다.
			string clientIdMessage = $"ID:{clientId}\n";
			byte[] clientIdBytes = Encoding.UTF8.GetBytes(clientIdMessage);

			// 메시지를 GZip으로 압축합니다.
			byte[] compressedClientIdBytes = Compress(clientIdBytes);
			await stream.WriteAsync(compressedClientIdBytes, 0, compressedClientIdBytes.Length);

			// 클라이언트로부터 메시지를 수신합니다.
			while ((byteCount = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
			{
				string received = Encoding.UTF8.GetString(buffer, 0, byteCount);
				messageBuilder.Append(received);

				string completeData = messageBuilder.ToString();
				int delimiterIndex;

				// 줄바꿈 문자를 기준으로 메시지를 분리합니다.
				while ((delimiterIndex = completeData.IndexOf('\n')) >= 0)
				{
					string singleMessage = completeData.Substring(0, delimiterIndex).Trim();
					messageBuilder.Remove(0, delimiterIndex + 1);
					Console.WriteLine($"클라이언트 #{clientId}로부터 받은 입력: {singleMessage}");

					// 수신된 메시지를 처리합니다.
					ProcessInput(clientId, singleMessage);
					completeData = messageBuilder.ToString();
				}
			}

			Console.WriteLine($"클라이언트 #{clientId} 연결 종료.");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"클라이언트 #{clientId} 처리 중 오류 발생: {ex.Message}");
		}
		finally
		{
			// 클라이언트 연결이 종료되면 해당 클라이언트를 제거합니다.
			RemoveClient(clientId);
			client.Close();
			Console.WriteLine($"클라이언트 #{clientId} 연결 해제됨.");
		}
	}

	/// <summary>
	/// 클라이언트를 딕셔너리에서 제거하고, 해당 클라이언트의 플레이어 데이터를 삭제합니다.
	/// </summary>
	/// <param name="clientId">제거할 클라이언트의 ID</param>
	private static void RemoveClient(uint clientId)
	{
		clients.TryRemove(clientId, out _);

		// 모든 씬에서 해당 플레이어를 제거합니다.
		foreach (var scene in scenes.Values)
		{
			scene.Players.TryRemove(clientId, out _);
		}
	}

	/// <summary>
	/// 클라이언트로부터 수신된 입력을 처리합니다.
	/// </summary>
	/// <param name="clientId">입력을 보낸 클라이언트의 ID</param>
	/// <param name="input">수신된 입력 문자열</param>
	private static void ProcessInput(uint clientId, string input)
	{
		// 플레이어가 속한 씬과 플레이어 데이터를 가져옵니다.
		if (!TryGetPlayerScene(clientId, out SceneData playerScene, out PlayerData player))
		{
			Console.WriteLine($"클라이언트 #{clientId}가 할당된 씬을 찾을 수 없습니다.");
			return;
		}

		// 입력 문자열을 공백으로 분리하여 각 명령어를 처리합니다.
		string[] inputs = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		foreach (string key in inputs)
		{
			switch (key.ToUpper())
			{
				case "UP":
					player.Y += player.Speed;
					break;
				case "DOWN":
					player.Y -= player.Speed;
					break;
				case "LEFT":
					player.X -= player.Speed;
					break;
				case "RIGHT":
					player.X += player.Speed;
					break;
				default:
					Console.WriteLine($"알 수 없는 입력: {key}");
					break;
			}
		}
	}

	/// <summary>
	/// 특정 클라이언트 ID로부터 플레이어와 해당 씬 정보를 가져옵니다.
	/// </summary>
	/// <param name="clientId">클라이언트의 ID</param>
	/// <param name="playerScene">플레이어가 속한 씬</param>
	/// <param name="player">플레이어 데이터</param>
	/// <returns>플레이어와 씬 정보를 성공적으로 가져왔는지 여부</returns>
	private static bool TryGetPlayerScene(uint clientId, out SceneData playerScene, out PlayerData player)
	{
		foreach (var scene in scenes.Values)
		{
			if (scene.Players.TryGetValue(clientId, out player))
			{
				playerScene = scene;
				return true;
			}
		}
		playerScene = null;
		player = null;
		return false;
	}

	/// <summary>
	/// 고유한 식별자를 생성합니다.
	/// </summary>
	/// <returns>생성된 고유 ID</returns>
	private static uint GenerateUniqueId()
	{
		return (uint)(DateTime.UtcNow.Ticks % uint.MaxValue);
	}

	/// <summary>
	/// 메인 게임 루프를 실행하여 게임 상태를 업데이트하고 클라이언트에게 전달합니다.
	/// </summary>
	private static async Task GameLoop()
	{
		const int targetFPS = 30;
		const int frameTime = 1000 / targetFPS;

		while (isRunning)
		{
			var frameStart = DateTime.UtcNow;

			// 현재 모든 씬의 상태를 복사하여 월드 상태를 생성합니다.
			WorldState currentWorldState = new WorldState
			{
				Worlds = new Dictionary<string, SceneData>(scenes)
			};

			// 월드 상태를 JSON으로 직렬화합니다.
			string worldStateJson = $"DATA:{JsonConvert.SerializeObject(currentWorldState)}\n";
			byte[] worldStateBytes = Encoding.UTF8.GetBytes(worldStateJson);

			// JSON 데이터를 압축합니다.
			byte[] compressedWorldStateBytes = Compress(worldStateBytes);

			// 모든 클라이언트에게 월드 상태를 브로드캐스트합니다.
			var broadcastTasks = clients.Values
				.Where(client => client.Connected)
				.Select(client => SendWorldStateAsync(client, compressedWorldStateBytes))
				.ToArray();

			await Task.WhenAll(broadcastTasks);

			// 연결이 끊긴 클라이언트를 감지하여 제거합니다.
			var disconnectedClients = clients.Where(kvp => !kvp.Value.Connected).Select(kvp => kvp.Key).ToList();
			foreach (var clientId in disconnectedClients)
			{
				Console.WriteLine($"클라이언트 #{clientId} 연결 끊김 감지됨.");
				RemoveClient(clientId);
			}

			// 플레이어가 맵 밖으로 나갔을 경우 씬을 전환합니다.
			foreach (var scene in scenes.Values)
			{
				var playersToMove = scene.Players.Values
					.Where(player => player.X > SceneWidth || player.X < -SceneWidth || player.Y > SceneHeight || player.Y < -SceneHeight)
					.ToList();

				foreach (var player in playersToMove)
				{
					// 현재 씬에서 플레이어를 제거합니다.
					scene.Players.TryRemove(player.NetworkId, out _);

					// 다른 씬으로 플레이어를 이동시킵니다.
					player.X = 0f;
					player.Y = 0f;
					player.SceneName = player.SceneName == "Scene_Gameplay_1" ? "Scene_Gameplay_2" : "Scene_Gameplay_1";
					scenes[player.SceneName].Players.TryAdd(player.NetworkId, player);
				}
			}

			// 플레이어와 별의 충돌을 감지하고 처리합니다.
			foreach (var scene in scenes.Values)
			{
				foreach (var player in scene.Players.Values)
				{
					foreach (var star in scene.Stars.Values)
					{
						if (IsCollision(player, star))
						{
							// 별의 위치를 랜덤하게 변경합니다.
							star.X = new Random().NextSingle() * (2f * SceneWidth) - SceneWidth;
							star.Y = new Random().NextSingle() * (2f * SceneHeight) - SceneHeight;

							// 플레이어의 점수를 증가시킵니다.
							player.Score += 1;
						}
					}
				}
			}

			// NPC의 움직임을 업데이트합니다.
			foreach (var scene in scenes.Values)
			{
				foreach (var npc in scene.Npcs.Values)
				{
					npc.X += new Random().Next(-1, 2);
					// 맵 경계를 넘어가지 않도록 제한합니다.
					if (npc.X > SceneWidth) npc.X = SceneWidth;
					if (npc.X < -SceneWidth) npc.X = -SceneWidth;
				}
			}

			// 타겟 프레임 시간을 유지하기 위해 딜레이를 추가합니다.
			var frameEnd = DateTime.UtcNow;
			int elapsed = (int)(frameEnd - frameStart).TotalMilliseconds;
			int delay = frameTime - elapsed;
			if (delay > 0)
			{
				await Task.Delay(delay);
			}
		}
	}

	/// <summary>
	/// 플레이어와 별 간의 충돌을 감지합니다.
	/// </summary>
	/// <param name="player">플레이어 데이터</param>
	/// <param name="star">별 데이터</param>
	/// <returns>충돌 여부</returns>
	private static bool IsCollision(PlayerData player, StarData star)
	{
		const float collisionDistance = 0.5f; // 충돌 거리 임계값

		float dx = player.X - star.X;
		float dy = player.Y - star.Y;

		return (dx * dx + dy * dy) <= (collisionDistance * collisionDistance);
	}

	/// <summary>
	/// 클라이언트에게 월드 상태를 전송합니다.
	/// </summary>
	/// <param name="client">클라이언트 TCP 연결</param>
	/// <param name="worldStateBytes">전송할 월드 상태의 바이트 배열</param>
	private static async Task SendWorldStateAsync(TcpClient client, byte[] worldStateBytes)
	{
		try
		{
			NetworkStream stream = client.GetStream();
			await stream.WriteAsync(worldStateBytes, 0, worldStateBytes.Length);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"클라이언트에게 월드 상태 전송 중 오류 발생: {ex.Message}");
		}
	}

	/// <summary>
	/// 바이트 배열 데이터를 GZip으로 압축합니다.
	/// </summary>
	/// <param name="data">압축할 데이터</param>
	/// <returns>압축된 바이트 배열</returns>
	private static byte[] Compress(byte[] data)
	{
		using (var compressedStream = new MemoryStream())
		{
			using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Compress))
			{
				gzipStream.Write(data, 0, data.Length);
			}
			return compressedStream.ToArray();
		}
	}

	/// <summary>
	/// 네트워크 엔티티의 기본 클래스입니다.
	/// </summary>
	public class BaseNetworkEntity
	{
		/// <summary>
		/// 엔티티의 고유 네트워크 ID
		/// </summary>
		public uint NetworkId { get; set; }

		/// <summary>
		/// 엔티티의 X 좌표
		/// </summary>
		public float X { get; set; }

		/// <summary>
		/// 엔티티의 Y 좌표
		/// </summary>
		public float Y { get; set; }

		/// <summary>
		/// 엔티티가 속한 씬의 이름
		/// </summary>
		public string SceneName { get; set; }
	}

	/// <summary>
	/// 플레이어 엔티티의 데이터 클래스입니다.
	/// </summary>
	public class PlayerData : BaseNetworkEntity
	{
		/// <summary>
		/// 플레이어의 이름
		/// </summary>
		public string Name { get; set; }

		/// <summary>
		/// 플레이어의 점수
		/// </summary>
		public int Score { get; set; }

		/// <summary>
		/// 플레이어의 이동 속도
		/// </summary>
		public float Speed { get; set; } = 0.05f;
	}

	/// <summary>
	/// 별 엔티티의 데이터 클래스입니다.
	/// </summary>
	public class StarData : BaseNetworkEntity
	{
	}

	/// <summary>
	/// NPC 엔티티의 데이터 클래스입니다.
	/// </summary>
	public class NpcData : BaseNetworkEntity
	{
		/// <summary>
		/// NPC의 이동 속도
		/// </summary>
		public float Speed { get; set; } = 0.1f;
	}

	/// <summary>
	/// 씬별 엔티티 데이터를 관리하는 클래스입니다.
	/// </summary>
	public class SceneData
	{
		/// <summary>
		/// 씬 내의 플레이어들을 관리하는 딕셔너리
		/// </summary>
		public ConcurrentDictionary<uint, PlayerData> Players { get; set; } = new ConcurrentDictionary<uint, PlayerData>();

		/// <summary>
		/// 씬 내의 별들을 관리하는 딕셔너리
		/// </summary>
		public ConcurrentDictionary<uint, StarData> Stars { get; set; } = new ConcurrentDictionary<uint, StarData>();

		/// <summary>
		/// 씬 내의 NPC들을 관리하는 딕셔너리
		/// </summary>
		public ConcurrentDictionary<uint, NpcData> Npcs { get; set; } = new ConcurrentDictionary<uint, NpcData>();
	}

	/// <summary>
	/// 모든 씬의 엔티티 상태를 포함하는 월드 상태 클래스입니다.
	/// </summary>
	public class WorldState
	{
		/// <summary>
		/// 씬 이름을 키로 하여 씬 데이터를 저장하는 딕셔너리
		/// </summary>
		public Dictionary<string, SceneData> Worlds { get; set; } = new Dictionary<string, SceneData>();
	}
}
