#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <Windows.h>
#else
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#endif

#include <cstring>
#include <cstdint>
#include <cerrno>
#include <string>
#include <thread>
#include <chrono>
#include <atomic>
#include <mutex>
#include <vector>

#include <GarrysMod/Lua/Interface.h>
#include <GarrysMod/FactoryLoader.hpp>
#include <ByteBuffer.hpp>
#include <Platform.hpp>
#include <color.h>
#include <eiface.h>

namespace Tier0
{
	typedef int32_t SpewRetval_t;
	typedef SpewRetval_t (*SpewOutputFunc_t)(int32_t spewType, const char* pMsg);

	struct LoggingContext_t
	{
		int32_t m_ChannelID;
		int32_t m_Flags;
		int32_t m_Severity;
		Color m_Color;
	};

	struct LoggingChannel_t
	{
		int32_t m_ID;
		int32_t m_Flags;
		int32_t m_MinimumSeverity;
		Color m_SpewColor;
		char m_Name[32];
		void* m_pFirstTag;
	};

	class ILoggingListener
	{
	public:
		virtual void Log(const LoggingContext_t* pContext, const char* pMessage) = 0;
	};

	static void (*SpewOutputFunc)(SpewOutputFunc_t func) = nullptr;
	static SpewOutputFunc_t (*GetSpewOutputFunc)() = nullptr;
	static const char* (*GetSpewOutputGroup)() = nullptr;
	static int (*GetSpewOutputLevel)() = nullptr;
	static const Color* (*GetSpewOutputColor)() = nullptr;

	static void (*LoggingSystem_RegisterLoggingListener)(ILoggingListener* pListener) = nullptr;
	static void (*LoggingSystem_UnregisterLoggingListener)(ILoggingListener* pListener) = nullptr;
	static void (*LoggingSystem_PushLoggingState)(bool bThreadLocal, bool bClearState) = nullptr;
	static void (*LoggingSystem_PopLoggingState)(bool bThreadLocal) = nullptr;
	static const LoggingChannel_t* (*LoggingSystem_GetChannel)(int32_t channelID) = nullptr;
}

#ifdef _WIN32
typedef SOCKET socket_t;
#define INVALID_SOCKET_VALUE INVALID_SOCKET
#define CloseSocket(s) closesocket(s)
#else
typedef int socket_t;
#define INVALID_SOCKET_VALUE (-1)
#define CloseSocket(s) close(s)
#endif

// GTerm always talks over a localhost TCP socket. The module is the server: it
// listens on this port and GTerm connects as a client.
static const uint16_t GTERM_PORT = 27514;
static const char* GTERM_HOST = "127.0.0.1";

static socket_t listenSocket = INVALID_SOCKET_VALUE;
static std::atomic<socket_t> clientSocket{ INVALID_SOCKET_VALUE };
static std::mutex sendMutex;

static volatile bool serverShutdown = false;
static volatile bool serverConnected = false;
static std::thread serverThread;

// Writes a single framed message to the connected client: a little-endian uint32
// length prefix followed by the message body. Framing makes the stream
// self-delimiting (replacing the old <EOL> sentinel) on every platform.
static void SendBuffer(const uint8_t* data, size_t size)
{
	socket_t sock = clientSocket.load();
	if (sock == INVALID_SOCKET_VALUE)
	{
		serverConnected = false;
		return;
	}

	std::lock_guard<std::mutex> lock(sendMutex);

	uint32_t length = static_cast<uint32_t>(size);
	uint8_t header[4] = {
		static_cast<uint8_t>(length & 0xFF),
		static_cast<uint8_t>((length >> 8) & 0xFF),
		static_cast<uint8_t>((length >> 16) & 0xFF),
		static_cast<uint8_t>((length >> 24) & 0xFF)
	};

	auto sendAll = [sock](const uint8_t* buf, size_t len) -> bool
	{
		size_t sent = 0;
		while (sent < len)
		{
#ifdef _WIN32
			int n = send(sock, reinterpret_cast<const char*>(buf + sent), static_cast<int>(len - sent), 0);
#else
			ssize_t n = send(sock, buf + sent, len - sent, MSG_NOSIGNAL);
#endif
			if (n <= 0)
				return false;

			sent += static_cast<size_t>(n);
		}

		return true;
	};

	if (!sendAll(header, sizeof(header)) || !sendAll(data, size))
		serverConnected = false;
}

class GTermConsoleListener : public Tier0::ILoggingListener
{
public:
	void Log(const Tier0::LoggingContext_t* pContext, const char* pMessage) override
	{
		const Tier0::LoggingChannel_t* chan = Tier0::LoggingSystem_GetChannel(pContext->m_ChannelID);
		const Color* color = &pContext->m_Color;
		int size = 12 + std::strlen(chan->m_Name) + std::strlen(pMessage);

		MultiLibrary::ByteBuffer buffer;
		buffer.Reserve(size);

		buffer <<
			chan->m_ID <<
			pContext->m_Severity <<
			chan->m_Name <<
			color->GetRawColor() <<
			pMessage;

		SendBuffer(buffer.GetBuffer(), buffer.Size());
	}
};

static GTermConsoleListener listener;
static bool useLoggingSystem = false;

template<typename T>
static bool ResolveSymbol(const SourceSDK::ModuleLoader& module, T& out, const char* name)
{
	out = reinterpret_cast<T>(module.GetSymbol(name));
	return out != nullptr;
}

static bool ResolveLoggingSystem(const SourceSDK::ModuleLoader& tier0)
{
	return
		ResolveSymbol(tier0, Tier0::LoggingSystem_RegisterLoggingListener, "LoggingSystem_RegisterLoggingListener") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_UnregisterLoggingListener, "LoggingSystem_UnregisterLoggingListener") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_PushLoggingState, "LoggingSystem_PushLoggingState") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_PopLoggingState, "LoggingSystem_PopLoggingState") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_GetChannel, "LoggingSystem_GetChannel");
}

static bool ResolveSpew(const SourceSDK::ModuleLoader& tier0)
{
	return
		ResolveSymbol(tier0, Tier0::SpewOutputFunc, "SpewOutputFunc") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputFunc, "GetSpewOutputFunc") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputGroup, "GetSpewOutputGroup") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputLevel, "GetSpewOutputLevel") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputColor, "GetSpewOutputColor");
}

static Tier0::SpewOutputFunc_t spewFunction = nullptr;
static Tier0::SpewRetval_t EngineSpewReceiver(int32_t type, const char* msg)
{
	if (!serverConnected)
		return spewFunction(type, msg);

	const Color* color = Tier0::GetSpewOutputColor();
	MultiLibrary::ByteBuffer buffer;
	buffer.Reserve(512);

	buffer <<
		type <<
		Tier0::GetSpewOutputLevel() <<
		Tier0::GetSpewOutputGroup() <<
		color->GetRawColor() <<
		msg;

	SendBuffer(buffer.GetBuffer(), buffer.Size());

	return spewFunction(type, msg);
}

static void RunCommand(std::string cmd)
{
	// in case the command hasnt been passed with a newline
	if (cmd.empty())
		return;

	if (cmd[cmd.length() - 1] != '\n')
		cmd.append("\n");

	SourceSDK::FactoryLoader engine_loader("engine");
	IVEngineServer* engine_server = engine_loader.GetInterface<IVEngineServer>(INTERFACEVERSION_VENGINESERVER);
	if (engine_server == nullptr) return;
	engine_server->ServerCommand(cmd.c_str());
}

// Reads exactly 'len' bytes from the socket into 'out'. Returns false on
// disconnect/error so the caller can drop the client.
static bool RecvAll(socket_t sock, uint8_t* out, size_t len)
{
	size_t received = 0;
	while (received < len)
	{
#ifdef _WIN32
		int n = recv(sock, reinterpret_cast<char*>(out + received), static_cast<int>(len - received), 0);
#else
		ssize_t n = recv(sock, out + received, len - received, 0);
#endif
		if (n > 0)
		{
			received += static_cast<size_t>(n);
			continue;
		}

		// n == 0 means the peer closed the connection: a real disconnect.
		if (n == 0)
			return false;

		// n < 0: the accepted socket inherits SO_RCVTIMEO from the listen
		// socket, so an idle read trips the timeout roughly every 250ms. That
		// is not a disconnect - keep waiting unless we're shutting down. Any
		// other error is a genuine failure and drops the client.
#ifdef _WIN32
		int err = WSAGetLastError();
		if (err == WSAETIMEDOUT || err == WSAEWOULDBLOCK || err == WSAEINTR)
#else
		if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)
#endif
		{
			if (serverShutdown)
				return false;

			continue;
		}

		return false;
	}

	return true;
}

// Owns the lifetime of the client socket: accepts a connection, reads framed
// commands until the client disconnects, then loops back to accept again.
static void ServerThread()
{
	while (!serverShutdown)
	{
		socket_t client = accept(listenSocket, nullptr, nullptr);
		if (client == INVALID_SOCKET_VALUE)
		{
			// timed out (SO_RCVTIMEO) or transient error; re-check shutdown
			continue;
		}

		clientSocket.store(client);
		serverConnected = true;

		while (!serverShutdown)
		{
			uint8_t header[4];
			if (!RecvAll(client, header, sizeof(header)))
				break;

			uint32_t length =
				static_cast<uint32_t>(header[0]) |
				(static_cast<uint32_t>(header[1]) << 8) |
				(static_cast<uint32_t>(header[2]) << 16) |
				(static_cast<uint32_t>(header[3]) << 24);

			if (length == 0)
				continue;

			std::vector<uint8_t> payload(length);
			if (!RecvAll(client, payload.data(), length))
				break;

			std::string cmd(payload.begin(), payload.end());
			RunCommand(cmd);
		}

		clientSocket.store(INVALID_SOCKET_VALUE);
		serverConnected = false;
		CloseSocket(client);
	}
}

GMOD_MODULE_OPEN()
{
	{
		SourceSDK::ModuleLoader tier0("tier0");
		useLoggingSystem = ResolveLoggingSystem(tier0);
		if (!useLoggingSystem && !ResolveSpew(tier0))
			LUA->ThrowError("failed to find a console output hook in tier0");
	}

#ifdef _WIN32
	WSADATA wsaData;
	if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
		LUA->ThrowError("failed to initialize Winsock");
#endif

	listenSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (listenSocket == INVALID_SOCKET_VALUE)
		LUA->ThrowError("failed to create socket");

	int reuse = 1;
#ifdef _WIN32
	setsockopt(listenSocket, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&reuse), sizeof(reuse));
#else
	setsockopt(listenSocket, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
#endif

	// Give accept() a timeout so ServerThread can observe serverShutdown.
#ifdef _WIN32
	DWORD timeout = 250; // milliseconds
	setsockopt(listenSocket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
#else
	struct timeval timeout;
	timeout.tv_sec = 0;
	timeout.tv_usec = 250000;
	setsockopt(listenSocket, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
#endif

	sockaddr_in addr;
	std::memset(&addr, 0, sizeof(addr));
	addr.sin_family = AF_INET;
	addr.sin_port = htons(GTERM_PORT);
	inet_pton(AF_INET, GTERM_HOST, &addr.sin_addr);

	if (bind(listenSocket, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0)
		LUA->ThrowError("failed to bind socket");

	if (listen(listenSocket, 1) != 0)
		LUA->ThrowError("failed to listen on socket");

	serverThread = std::thread(ServerThread);

	if (useLoggingSystem)
	{
		Tier0::LoggingSystem_PushLoggingState(false, false);
		Tier0::LoggingSystem_RegisterLoggingListener(&listener);
	}
	else
	{
		spewFunction = Tier0::GetSpewOutputFunc();
		Tier0::SpewOutputFunc(EngineSpewReceiver);
	}

	return 0;
}

GMOD_MODULE_CLOSE()
{
	if (useLoggingSystem)
	{
		Tier0::LoggingSystem_UnregisterLoggingListener(&listener);
		Tier0::LoggingSystem_PopLoggingState(false);
	}
	else if (spewFunction != nullptr)
	{
		Tier0::SpewOutputFunc(spewFunction);
	}

	serverShutdown = true;

	// Closing the listen socket unblocks accept() if it is mid-call.
	if (listenSocket != INVALID_SOCKET_VALUE)
		CloseSocket(listenSocket);

	if (serverThread.joinable())
		serverThread.join();

	socket_t client = clientSocket.exchange(INVALID_SOCKET_VALUE);
	if (client != INVALID_SOCKET_VALUE)
		CloseSocket(client);

#ifdef _WIN32
	WSACleanup();
#endif

	return 0;
}
