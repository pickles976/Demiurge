using Riptide;
using Riptide.Utils;
using Demiurge;



Dictionary<ushort, string> players = new Dictionary<ushort, string>();

// Start
RiptideLogger.Initialize(Console.WriteLine, Console.WriteLine, Console.WriteLine, Console.WriteLine, includeTimestamps: true);

Server server = new Server();

server.ClientConnected += (object sender, ServerConnectedEventArgs e) =>
{
    Message msg = Message.Create(MessageSendMode.Reliable, (ushort)ServerToClientId.Welcome);
    msg.AddString($"Welcome! You are client {e.Client.Id}.");
    server.Send(msg, e.Client.Id);

    foreach (ushort id in players.Keys)
    {
        if (id != e.Client.Id)
        {
            server.Send(Message.Create(MessageSendMode.Reliable, ServerToClientId.SpawnPlayer), id);
        }
    }


};

server.Start(NetworkConfig.Port, maxClientCount: 100);

bool running = true;
Console.CancelKeyPress += (_,e) => {e.Cancel = true; running = false; };

// Update
while (running)
{
    server.Update();
    Thread.Sleep(10);
}

server.Stop();
