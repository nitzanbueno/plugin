using System;
using System.Collections.Generic;
using System.Linq;
using MusicBeeRemote.Core.ApiAdapters;
using MusicBeeRemote.Core.Events;
using MusicBeeRemote.Core.Events.Status.Internal;
using MusicBeeRemote.Core.Model.Entities;
using MusicBeeRemote.Core.Network;
using Newtonsoft.Json.Linq;
using TinyMessenger;

namespace MusicBeeRemote.Core.Commands.Requests.Playlists
{
    public class RequestPlaylistListSongs : ICommand
    {
        private readonly ILibraryApiAdapter _libraryApiAdapter;
        private readonly ITinyMessengerHub _hub;

        public RequestPlaylistListSongs(ILibraryApiAdapter libraryApiAdapter, ITinyMessengerHub hub)
        {
            _libraryApiAdapter = libraryApiAdapter;
            _hub = hub;
        }

        public void Execute(IEvent receivedEvent)
        {
            if (receivedEvent == null)
            {
                throw new ArgumentNullException(nameof(receivedEvent));
            }

            IEnumerable<Track> tracks = null;

            var token = receivedEvent.DataToken();
            if (token != null && token.Type == JTokenType.String)
            {
                var path = token.Value<string>();

                IEnumerable<string> trackPaths = _libraryApiAdapter.GetPlaylistTrackPaths(path);

                tracks = _libraryApiAdapter.GetTracks(trackPaths.ToArray());
            }

            var message = new SocketMessage
            {
                Context = Constants.PlaylistListSongs,
                Data = tracks,
                NewLineTerminated = true,
            };

            _hub.Publish(new PluginResponseAvailableEvent(message, receivedEvent.ConnectionId));
        }
    }
}
