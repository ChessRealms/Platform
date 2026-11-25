using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

using ChessRealms.Platform.Api.Controllers;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class GameLobbyTests : GameApiTestBase
{
    [Fact]
    public async Task SimultaneousLobbyJoinsCreateOneGameAndTheWinnerCanRetryAfterExpiry()
    {
        (Guid hostId, string hostToken) = await RegisterAndSignInAsync("host");
        (Guid guestId, string guestToken) = await RegisterAndSignInAsync("guest");
        (Guid otherId, string otherToken) = await RegisterAndSignInAsync("other");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);
        using HttpClient other = CreateClient(otherToken);
        using HttpClient anonymous = CreateClient();
        LobbyResponse lobby = await CreateLobbyAsync(host);
        string url = $"/api/lobbies/{lobby.Id}";

        using HttpResponseMessage anonymousRead = await anonymous.GetAsync(url, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        LobbyResponse preview = Assert.IsType<LobbyResponse>(await guest.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal("host", preview.HostName);
        Assert.False(preview.IsOwner);
        Assert.Equal("Open", preview.Status);
        Assert.Null(preview.GameId);
        Assert.Empty((await ListGamesAsync(guest)).Items);
        Assert.Empty((await ListGamesAsync(host, "?status=Active")).Items);
        GameListItemResponse invitation = Assert.Single((await ListGamesAsync(host)).Items);
        Assert.Equal(lobby.Id, invitation.Id);
        Assert.True(invitation.IsLobby);
        Assert.Null(invitation.OpponentId);
        using HttpResponseMessage selfJoin = await host.PostAsJsonAsync($"{url}/join", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(selfJoin, HttpStatusCode.Forbidden, "Forbidden");
        using HttpResponseMessage guestCancel = await guest.PostAsJsonAsync($"{url}/cancel", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(guestCancel, HttpStatusCode.Forbidden, "Forbidden");

        GameRevisionRequest revision = new(lobby.Revision);
        Task<HttpResponseMessage> first = guest.PostAsJsonAsync($"{url}/join", revision, Cancellation);
        Task<HttpResponseMessage> second = other.PostAsJsonAsync($"{url}/join", revision, Cancellation);
        using HttpResponseMessage guestResponse = await first;
        using HttpResponseMessage otherResponse = await second;
        HttpResponseMessage[] responses = [guestResponse, otherResponse];
        HttpResponseMessage success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        await AssertErrorAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "LobbyAlreadyJoined");
        LobbyResponse joined = Assert.IsType<LobbyResponse>(await success.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Assert.Equal("Joined", joined.Status);
        Assert.Equal(1, joined.Revision);
        Guid gameId = Assert.IsType<Guid>(joined.GameId);
        HttpClient winner = success == guestResponse ? guest : other;
        HttpClient loser = success == guestResponse ? other : guest;

        Clock.Advance(TimeSpan.FromMinutes(5));
        using HttpResponseMessage retry = await winner.PostAsJsonAsync($"{url}/join", revision, Cancellation);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(joined with { ServerTime = Clock.GetUtcNow() }, await retry.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        LobbyResponse hostView = Assert.IsType<LobbyResponse>(await host.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal(gameId, hostView.GameId);
        using HttpResponseMessage loserRead = await loser.GetAsync(url, Cancellation);
        await AssertErrorAsync(loserRead, HttpStatusCode.Conflict, "LobbyAlreadyJoined");
        using HttpResponseMessage loserBoard = await loser.GetAsync($"/api/games/{gameId}/board", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, loserBoard.StatusCode);

        GameResponse active = (await ReadBoardAsync(winner, $"/api/games/{gameId}")).Game;
        Assert.Equal((hostId, success == guestResponse ? guestId : otherId), (active.WhitePlayerId, active.BlackPlayerId));
        Assert.Equal("Active", active.Status);
        Assert.Equal(0, active.Revision);
        Assert.Equal(gameId, Assert.Single((await ListGamesAsync(host, "?status=Active")).Items).Id);
        Assert.Empty((await ListGamesAsync(host)).Items);
        Assert.Empty((await ListGamesAsync(host, "?status=ClosedInvitations")).Items);
        GameResponse moved = await MakeMoveAsync(host, $"/api/games/{gameId}", "e2e4", active.Revision);
        Assert.Equal(moved, (await ReadBoardAsync(winner, $"/api/games/{gameId}")).Game);
    }

    [Fact]
    public async Task JoiningAndCancellingTheSameLobbyCommitOnlyOneOutcome()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (_, string guestToken) = await RegisterAndSignInAsync("guest");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);
        LobbyResponse lobby = await CreateLobbyAsync(host);
        string url = $"/api/lobbies/{lobby.Id}";
        GameRevisionRequest revision = new(lobby.Revision);

        Task<HttpResponseMessage> join = guest.PostAsJsonAsync($"{url}/join", revision, Cancellation);
        Task<HttpResponseMessage> cancel = host.PostAsJsonAsync($"{url}/cancel", revision, Cancellation);
        using HttpResponseMessage joinResponse = await join;
        using HttpResponseMessage cancelResponse = await cancel;
        HttpResponseMessage[] responses = [joinResponse, cancelResponse];
        HttpResponseMessage success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        await AssertErrorAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "RevisionConflict");
        LobbyResponse committed = Assert.IsType<LobbyResponse>(await success.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Assert.Equal(1, committed.Revision);
        Assert.Equal(success == joinResponse ? "Joined" : "Cancelled", committed.Status);
        Assert.Equal(committed with { IsOwner = true }, await host.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal(committed with { IsOwner = false }, await guest.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal(success == joinResponse ? 1 : 0, (await ListGamesAsync(host, "?status=Active")).Items.Count);
    }

    [Fact]
    public async Task LobbiesShareTheOutgoingQuotaAndExpireBeforeJoinOrCancel()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (Guid guestId, string guestToken) = await RegisterAndSignInAsync("guest");
        (Guid otherId, _) = await RegisterAndSignInAsync("other");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);
        GameResponse direct = await CreateGameAsync(host, guestId);
        CreateLobbyRequest request = new(Guid.NewGuid());
        LobbyResponse first = await CreateLobbyAsync(host, request);
        Assert.Equal(Clock.GetUtcNow().AddMinutes(5), first.ExpiresAt);
        Assert.Equal(Clock.GetUtcNow(), first.ServerTime);

        Task<HttpResponseMessage> nextLink = host.PostAsJsonAsync("/api/lobbies", new CreateLobbyRequest(Guid.NewGuid()), Cancellation);
        Task<HttpResponseMessage> nextDirect = host.PostAsJsonAsync("/api/games", new CreateGameRequest(otherId), Cancellation);
        using HttpResponseMessage next = await nextLink;
        using HttpResponseMessage another = await nextDirect;
        HttpResponseMessage[] responses = [next, another];
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        await AssertErrorAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "InvitationLimitReached");
        GameListResponse invitations = await ListGamesAsync(host);
        Assert.Equal(3, invitations.Items.Count);
        Assert.Contains(invitations.Items, item => !item.IsLobby && item.Id == direct.Id);
        using HttpResponseMessage replay = await host.PostAsJsonAsync("/api/lobbies", request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first, await replay.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));

        Clock.Advance(TimeSpan.FromMinutes(5));
        string url = $"/api/lobbies/{first.Id}";
        using HttpResponseMessage join = await guest.PostAsJsonAsync($"{url}/join", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(join, HttpStatusCode.Gone, "InvitationExpired");
        using HttpResponseMessage cancel = await host.PostAsJsonAsync($"{url}/cancel", new GameRevisionRequest(0), Cancellation);
        await AssertErrorAsync(cancel, HttpStatusCode.Gone, "InvitationExpired");
        LobbyResponse expired = Assert.IsType<LobbyResponse>(await guest.GetFromJsonAsync<LobbyResponse>(url, Cancellation));
        Assert.Equal("Expired", expired.Status);
        Assert.Equal(first.ExpiresAt, expired.ServerTime);
        Assert.Null(expired.GameId);
        Assert.Empty((await ListGamesAsync(host)).Items);
        Assert.Equal(3, (await ListGamesAsync(host, "?status=ClosedInvitations")).Items.Count);
        await CreateLobbyAsync(host);
    }

    [Fact]
    public async Task JoinedLobbiesDoNotConsumeTheDirectInvitationWindow()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (Guid guestId, string guestToken) = await RegisterAndSignInAsync("guest");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);

        for (int i = 0; i < 5; i++)
        {
            LobbyResponse lobby = await CreateLobbyAsync(host);
            using HttpResponseMessage join = await guest.PostAsJsonAsync($"/api/lobbies/{lobby.Id}/join",
                new GameRevisionRequest(lobby.Revision), Cancellation);
            Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        }

        Assert.Equal(5, (await ListGamesAsync(host, "?status=Active")).Items.Count);
        await CreateGameAsync(host, guestId);
    }

    [Fact]
    public async Task APrivateLinkKeepsItsTimeControlUntilJoin()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (_, string guestToken) = await RegisterAndSignInAsync("guest");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateClient(guestToken);
        CreateLobbyRequest request = new(Guid.NewGuid(), "Blitz5");
        LobbyResponse lobby = await CreateLobbyAsync(host, request);
        Assert.Equal("Blitz5", lobby.TimeControl);
        using HttpResponseMessage mismatch = await host.PostAsJsonAsync("/api/lobbies", request with { TimeControl = null }, Cancellation);
        await AssertErrorAsync(mismatch, HttpStatusCode.Conflict, "CreationRequestConflict");
        Clock.Advance(TimeSpan.FromMinutes(1));

        string url = $"/api/lobbies/{lobby.Id}";
        Assert.Equal("Blitz5", (await guest.GetFromJsonAsync<LobbyResponse>(url, Cancellation))?.TimeControl);
        using HttpResponseMessage joined = await SendActionAsync(guest, url, "join", lobby.Revision);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        LobbyResponse result = Assert.IsType<LobbyResponse>(await joined.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        GameResponse active = (await ReadBoardAsync(guest, $"/api/games/{result.GameId}")).Game;
        Assert.Equal(Clock.GetUtcNow().AddMinutes(5), active.Clock!.TurnDeadline);
    }

    [Fact]
    public async Task GuestSessionOwnsTheSeatAndCanRecoverAfterTheLinkExpires()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient guest = CreateGuestClient();
        using HttpClient stranger = CreateGuestClient();
        LobbyResponse lobby = await CreateLobbyAsync(host, new(Guid.NewGuid(), "Blitz5"));
        string link = $"/api/lobbies/{lobby.Id}";

        LobbyResponse preview = Assert.IsType<LobbyResponse>(await guest.GetFromJsonAsync<LobbyResponse>($"{link}/guest", Cancellation));
        Assert.Equal("host", preview.HostName);
        Assert.Equal("Blitz5", preview.TimeControl);
        Assert.Null(preview.GameId);
        using HttpResponseMessage unauthenticatedJoin = await SendActionAsync(guest, link, "join", preview.Revision);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedJoin.StatusCode);
        using HttpResponseMessage invalidName = await guest.PostAsJsonAsync("/api/guests/session",
            new CreateGuestSessionRequest(lobby.Id, "  "), Cancellation);
        await AssertErrorAsync(invalidName, HttpStatusCode.BadRequest, "InvalidGuestName");

        GuestProfile profile = await StartGuestAsync(guest, lobby.Id, "  Guest Knight  ");
        Assert.Equal("Guest Knight", profile.Username);
        await StartGuestAsync(stranger, lobby.Id, profile.Username);
        using HttpResponseMessage join = await SendActionAsync(guest, link, "join", preview.Revision);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        LobbyResponse joined = Assert.IsType<LobbyResponse>(await join.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Guid gameId = Assert.IsType<Guid>(joined.GameId);
        string game = $"/api/games/{gameId}";
        GameBoardResponse aboard = await ReadBoardAsync(guest, game);
        Assert.Equal(profile.UserId, aboard.Game.BlackPlayerId);
        Assert.Equal(profile.Username, aboard.Game.BlackGuestName);
        Assert.Equal("Blitz5", aboard.Game.Clock?.TimeControl);
        Assert.Equal(profile.Username, Assert.Single((await ListGamesAsync(host, "?status=Active")).Items).OpponentName);

        JsonElement liveGrant = await guest.GetFromJsonAsync<JsonElement>("/api/guests/session/live", Cancellation);
        string proof = liveGrant.GetProperty("accessToken").GetString()!;
        using (ClientWebSocket otherSocket = new())
        {
            otherSocket.Options.SetRequestHeader("Cookie", stranger.DefaultRequestHeaders.GetValues("Cookie").Single());
            await Assert.ThrowsAsync<WebSocketException>(() => otherSocket.ConnectAsync(LiveUri(proof), Cancellation));
        }
        using (ClientWebSocket guestSocket = new())
        {
            guestSocket.Options.SetRequestHeader("Cookie", guest.DefaultRequestHeaders.GetValues("Cookie").Single());
            await ConnectLiveAsync(guestSocket, proof);
            JsonElement subscribed = await SubscribeAsync(guestSocket, gameId);
            Assert.False(subscribed.TryGetProperty("error", out _));
        }

        using HttpResponseMessage otherPreview = await stranger.GetAsync($"{link}/guest", Cancellation);
        await AssertErrorAsync(otherPreview, HttpStatusCode.Conflict, "LobbyAlreadyJoined");
        using HttpResponseMessage otherBoard = await stranger.GetAsync($"{game}/board", Cancellation);
        await AssertErrorAsync(otherBoard, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage otherMove = await PlayAsync(stranger, game, "e7e5", 1);
        await AssertErrorAsync(otherMove, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage createLink = await guest.PostAsJsonAsync("/api/lobbies", new CreateLobbyRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, createLink.StatusCode);
        using HttpResponseMessage guestList = await guest.GetAsync("/api/games?status=Active", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, guestList.StatusCode);
        using HttpResponseMessage guestQuickPlay = await guest.PostAsJsonAsync("/api/quick-play",
            new JoinQuickPlayRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, guestQuickPlay.StatusCode);

        GameResponse whiteMove = await MakeMoveAsync(host, game, "e2e4", aboard.Game.Revision);
        using (HttpRequestMessage crossOrigin = new(HttpMethod.Post, $"{game}/moves")
        {
            Content = JsonContent.Create(new PlayGameMoveRequest(whiteMove.Revision, "e7e5"))
        })
        {
            crossOrigin.Headers.Add("Sec-Fetch-Site", "same-site");
            using HttpResponseMessage denied = await guest.SendAsync(crossOrigin, Cancellation);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        GameResponse blackMove = await MakeMoveAsync(guest, game, "e7e5", whiteMove.Revision);
        GameResponse finished = await ApplyActionAsync(guest, game, "resign", blackMove.Revision);
        GameReplayResponse replay = await ReadReplayAsync(guest, game);
        Assert.Equal("Black", replay.ViewerColor);
        Assert.Equal(["e2e4", "e7e5"], replay.Moves.Select(move => move.Move));
        GameResponse offer = await ApplyActionAsync(host, game, "rematch/offer", finished.Revision);
        GameResponse rematched = await ApplyActionAsync(guest, game, "rematch/accept", offer.Revision);
        GameResponse rematch = (await ReadBoardAsync(guest, $"/api/games/{rematched.RematchGameId}")).Game;
        Assert.Equal(profile.UserId, rematch.WhitePlayerId);
        Assert.Equal(profile.Username, rematch.WhiteGuestName);

        Clock.Advance(TimeSpan.FromMinutes(6));
        using HttpResponseMessage repeated = await SendActionAsync(guest, link, "join", preview.Revision);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(gameId, (await repeated.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation))?.GameId);

        Clock.Advance(TimeSpan.FromDays(29));
        using HttpResponseMessage renewed = await guest.GetAsync("/api/guests/session", Cancellation);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.True(renewed.Headers.Contains("Set-Cookie"));
        Assert.Equal(profile, await renewed.Content.ReadFromJsonAsync<GuestProfile>(Cancellation));
        Clock.Advance(TimeSpan.FromDays(1));
        using HttpResponseMessage expired = await stranger.GetAsync("/api/guests/session", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        using HttpResponseMessage revoke = await guest.DeleteAsync("/api/guests/session", Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using HttpResponseMessage revokedBoard = await guest.GetAsync($"{game}/board", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedBoard.StatusCode);
    }

    [Fact]
    public async Task GuestAndAccountJoiningTogetherCanCreateOnlyOneGame()
    {
        (_, string hostToken) = await RegisterAndSignInAsync("host");
        (_, string memberToken) = await RegisterAndSignInAsync("member");
        using HttpClient host = CreateClient(hostToken);
        using HttpClient member = CreateClient(memberToken);
        using HttpClient guest = CreateGuestClient();
        LobbyResponse lobby = await CreateLobbyAsync(host);
        await StartGuestAsync(guest, lobby.Id, "Guest");
        string url = $"/api/lobbies/{lobby.Id}";

        HttpResponseMessage[] responses = await Task.WhenAll(
            SendActionAsync(guest, url, "join", lobby.Revision),
            SendActionAsync(member, url, "join", lobby.Revision));
        using HttpResponseMessage winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        using HttpResponseMessage loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await AssertErrorAsync(loser, HttpStatusCode.Conflict, "LobbyAlreadyJoined");
        LobbyResponse joined = Assert.IsType<LobbyResponse>(await winner.Content.ReadFromJsonAsync<LobbyResponse>(Cancellation));
        Assert.Equal(joined.GameId, Assert.Single((await ListGamesAsync(host, "?status=Active")).Items).Id);
    }

    private HttpClient CreateGuestClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add("X-ChessRealms-Web", "1");
        return client;
    }

    private async Task<GuestProfile> StartGuestAsync(HttpClient client, Guid lobbyId, string name)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/guests/session",
            new CreateGuestSessionRequest(lobbyId, name), Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Equal("__Host-ChessRealms.Guest", cookie.Split('=', 2)[0]);
        Assert.True(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.True(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.True(cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie.Split(';')[0]);
        return Assert.IsType<GuestProfile>(await response.Content.ReadFromJsonAsync<GuestProfile>(Cancellation));
    }
}
