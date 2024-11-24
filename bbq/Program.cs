using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

class Program
{
	// 연결된 클라이언트를 저장하는 스레드-안전한 컬렉션
	private static readonly ConcurrentDictionary<uint, TcpClient> clients = new ConcurrentDictionary<uint, TcpClient>();
	private static uint clientIdCounter = 0;

	// 각 씬별 엔티티 상태를 저장하는 스레드-안전한 컬렉션
	private static readonly ConcurrentDictionary<string, SceneData> scenes = new ConcurrentDictionary<string, SceneData>();

	// 게임 루프 실행 여부를 제어하는 플래그
	private static bool isRunning = true;

	// 씬 할당을 위한 카운터 (라운드 로빈 방식)
	private static int sceneAssignCounter = 0;

	static async Task Main(string[] args)
	{
		// 초기 씬 생성
		InitializeScenes();

		// TCP 리스너 초기화 및 시작
		TcpListener listener = new TcpListener(IPAddress.Any, 3100);
		listener.Start();
		Console.WriteLine("서버가 포트 3100에서 시작되었습니다.");

		// 게임 루프 비동기 시작
		_ = Task.Run(GameLoop);

		// 클라이언트 연결 수락 루프
		while (isRunning)
		{
			try
			{
				TcpClient client = await listener.AcceptTcpClientAsync();
				clientIdCounter++;
				uint clientId = clientIdCounter;

				// 클라이언트 추가
				if (clients.TryAdd(clientId, client))
				{
					Console.WriteLine($"클라이언트 #{clientId} 연결됨.");

					// 씬 할당 (라운드 로빈 방식)
					string assignedSceneName = AssignScene();
					SceneData scene = scenes[assignedSceneName];

					// 기본 플레이어 상태 추가
					PlayerData player = new PlayerData
					{
						NetworkId = clientId,
						X = 0,
						Y = 0,
						SceneName = assignedSceneName
					};
					scene.Players.TryAdd(clientId, player);
					Console.WriteLine($"클라이언트 #{clientId}가 '{assignedSceneName}'에 할당되었습니다.");

					// 클라이언트 처리 비동기 시작
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

		// 리스너 중지
		listener.Stop();
	}

	// 초기 씬을 생성하는 메서드
	private static void InitializeScenes()
	{
		scenes.TryAdd("Scene_Gameplay_1", new SceneData());
		scenes.TryAdd("Scene_Gameplay_2", new SceneData());
		Console.WriteLine("기본 씬 'Scene_Gameplay_1'과 'Scene_Gameplay_2'가 초기화되었습니다.");
	}

	// 씬을 할당하는 메서드 (라운드 로빈 방식)
	private static string AssignScene()
	{
		var sceneNames = new List<string>(scenes.Keys);
		string assignedScene = sceneNames[sceneAssignCounter % sceneNames.Count];
		sceneAssignCounter++;
		return assignedScene;
	}

	// 연결된 클라이언트와의 통신을 처리하는 메서드
	private static async Task HandleClientAsync(TcpClient client, uint clientId)
	{
		// 타임아웃 설정 (10초)
		client.ReceiveTimeout = 10000; // 10초
		client.SendTimeout = 10000; // 10초

		StringBuilder messageBuilder = new StringBuilder();

		try
		{
			using NetworkStream stream = client.GetStream();
			byte[] buffer = new byte[1024];
			int byteCount;

			// 클라이언트에게 환영 메시지와 클라이언트 ID 전송
			string welcomeMessage = $"MSG:클라이언트 #{clientId}에 연결되었습니다.\n";
			string clientIdMessage = $"ID:{clientId}\n";
			byte[] welcomeBytes = Encoding.UTF8.GetBytes(welcomeMessage + clientIdMessage);

			// 메시지를 gzip으로 압축
			byte[] compressedWelcomeBytes = Compress(welcomeBytes);
			await stream.WriteAsync(compressedWelcomeBytes, 0, compressedWelcomeBytes.Length);

			// 클라이언트로부터의 메시지 수신 루프
			while ((byteCount = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
			{
				string received = Encoding.UTF8.GetString(buffer, 0, byteCount);
				messageBuilder.Append(received);

				string completeData = messageBuilder.ToString();
				int delimiterIndex;
				while ((delimiterIndex = completeData.IndexOf('\n')) >= 0)
				{
					string singleMessage = completeData.Substring(0, delimiterIndex).Trim();
					messageBuilder.Remove(0, delimiterIndex + 1);
					Console.WriteLine($"클라이언트 #{clientId}로부터 받은 입력: {singleMessage}");

					// 클라이언트로부터 받은 입력 처리
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
			// 클라이언트 연결 해제 시 컬렉션에서 제거
			RemoveClient(clientId);
			client.Close();
			Console.WriteLine($"클라이언트 #{clientId} 연결 해제됨.");
		}
	}

	// 클라이언트를 컬렉션에서 제거하는 메서드
	private static void RemoveClient(uint clientId)
	{
		clients.TryRemove(clientId, out _);

		// 모든 씬에서 해당 플레이어 제거
		foreach (var scene in scenes.Values)
		{
			scene.Players.TryRemove(clientId, out _);
		}
	}

	// 클라이언트로부터 받은 입력을 처리하는 메서드
	private static void ProcessInput(uint clientId, string input)
	{
		// 플레이어가 속한 씬과 플레이어 데이터 가져오기
		if (!TryGetPlayerScene(clientId, out SceneData playerScene, out PlayerData player))
		{
			Console.WriteLine($"클라이언트 #{clientId}가 할당된 씬을 찾을 수 없습니다.");
			return;
		}

		string[] inputs = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		foreach (string key in inputs)
		{
			switch (key.ToUpper())
			{
				case "UP":
					player.Y += 0.05f;
					break;
				case "DOWN":
					player.Y -= 0.05f;
					break;
				case "LEFT":
					player.X -= 0.05f;
					break;
				case "RIGHT":
					player.X += 0.05f;
					break;
				default:
					Console.WriteLine($"알 수 없는 입력: {key}");
					break;
			}
		}
	}

	// 플레이어가 속한 씬과 플레이어 데이터를 가져오는 메서드
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

	// 새로운 별을 생성하고 관리하는 메서드
	//private static void FireBullet(uint ownerId, SceneData scene)
	//{
	//	// 총알 ID 생성
	//	uint bulletId = GenerateUniqueId();

	//	if (!scene.Players.TryGetValue(ownerId, out PlayerData player))
	//	{
	//		Console.WriteLine($"플레이어 #{ownerId}를 찾을 수 없습니다.");
	//		return;
	//	}

	//	// 총알 생성
	//	BulletData bullet = new BulletData
	//	{
	//		NetworkId = bulletId,
	//		OwnerId = ownerId,
	//		X = player.X,
	//		Y = player.Y,
	//		SceneName = player.SceneName
	//	};
	//	scene.Bullets.TryAdd(bulletId, bullet);
	//}

	// 고유한 식별자를 생성하는 메서드
	private static uint GenerateUniqueId()
	{
		return (uint)(DateTime.UtcNow.Ticks % uint.MaxValue);
	}

	// 메인 게임 루프를 실행하는 메서드
	private static async Task GameLoop()
	{
		const int targetFPS = 30;
		const int frameTime = 1000 / targetFPS;

		while (isRunning)
		{
			var frameStart = DateTime.UtcNow;

			// 모든 씬의 월드 상태 생성
			WorldState currentWorldState = new WorldState
			{
				Worlds = new Dictionary<string, SceneData>(scenes)
			};

			// 월드 상태를 JSON으로 직렬화
			string worldStateJson = $"DATA:{JsonConvert.SerializeObject(currentWorldState)}\n";
			byte[] worldStateBytes = Encoding.UTF8.GetBytes(worldStateJson);

			// JSON 데이터를 압축
			byte[] compressedWorldStateBytes = Compress(worldStateBytes);

			// 모든 클라이언트에게 월드 상태 브로드캐스트
			var broadcastTasks = clients.Values
				.Where(client => client.Connected)
				.Select(client => SendWorldStateAsync(client, compressedWorldStateBytes))
				.ToArray();

			await Task.WhenAll(broadcastTasks);

			// 클라이언트 연결 상태 확인
			var disconnectedClients = clients.Where(kvp => !kvp.Value.Connected).Select(kvp => kvp.Key).ToList();
			foreach (var clientId in disconnectedClients)
			{
				Console.WriteLine($"클라이언트 #{clientId} 연결 끊김 감지됨.");
				RemoveClient(clientId);
			}

			// 플레이어가 맵 밖에 나가면 씬 전환
			foreach (var scene in scenes.Values)
			{
				var playersToMove = new List<PlayerData>();

				foreach (var player in scene.Players.Values)
				{
					if (player.X > 10f || player.X < -10f || player.Y > 10f || player.Y < -10f)
					{
						playersToMove.Add(player);
					}
				}

				foreach (var player in playersToMove)
				{
					// 현재 씬에서 플레이어 제거
					scene.Players.TryRemove(player.NetworkId, out _);

					// 새로운 씬으로 플레이어 이동
					player.X = 0f;
					player.Y = 0f;
					string newSceneName = player.SceneName == "Scene_Gameplay_1" ? "Scene_Gameplay_2" : "Scene_Gameplay_1";
					player.SceneName = newSceneName;
					scenes[newSceneName].Players.TryAdd(player.NetworkId, player);
				}
			}

			// 추가적인 월드 시뮬레이션 로직 (NPC 동작 등)을 여기에 추가할 수 있습니다.

			// 목표 프레임 시간 유지
			var frameEnd = DateTime.UtcNow;
			int elapsed = (int)(frameEnd - frameStart).TotalMilliseconds;
			int delay = frameTime - elapsed;
			if (delay > 0)
			{
				await Task.Delay(delay);
			}
		}
	}

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

	// 데이터를 압축하는 메서드
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

	// 모든 네트워크 엔티티의 기본 클래스
	public class BaseNetworkEntity
	{
		public uint NetworkId { get; set; }
		public float X { get; set; }
		public float Y { get; set; }
		public string SceneName { get; set; }
	}

	// 플레이어 엔티티 데이터 클래스
	public class PlayerData : BaseNetworkEntity
	{
		// 추가적인 플레이어 속성이나 메서드가 필요하다면 여기에 정의
	}

	// 총알 엔티티 데이터 클래스
	public class BulletData : BaseNetworkEntity
	{
		public uint OwnerId { get; set; } // 총알을 발사한 플레이어 ID
	}

	// NPC 엔티티 데이터 클래스
	public class NpcData : BaseNetworkEntity
	{
		// 추가적인 NPC 속성이나 메서드가 필요하다면 여기에 정의
	}

	// 씬별 엔티티 데이터를 관리하는 클래스
	public class SceneData
	{
		public ConcurrentDictionary<uint, PlayerData> Players { get; set; } = new ConcurrentDictionary<uint, PlayerData>();
		public ConcurrentDictionary<uint, BulletData> Bullets { get; set; } = new ConcurrentDictionary<uint, BulletData>();
		public ConcurrentDictionary<uint, NpcData> Npcs { get; set; } = new ConcurrentDictionary<uint, NpcData>();
	}

	// 모든 엔티티를 포함하는 월드 상태 클래스
	public class WorldState
	{
		public Dictionary<string, SceneData> Worlds { get; set; } = new Dictionary<string, SceneData>();
	}
}
