﻿using MusicBeeRemote.Core.Enumerations;
using MusicBeeRemote.Core.Model.Entities;
using MusicBeeRemote.Core.Monitoring;

namespace MusicBeeRemote.Core.ApiAdapters
{
    public interface ITrackApiAdapter
    {
        TrackTemporalnformation GetTemporalInformation();

        SupportTrackTemporalnformation GetSupportTemporalInformation();

        bool SeekTo(int position);

        string GetLyrics();

        string GetCover();

        NowPlayingTrack GetPlayingTrackInfoLegacy();

        NowPlayingTrackV2 GetPlayingTrackInfo();

        string SetRating(string rating);

        string GetRating();

        LastfmStatus ChangeStatus(string action);

        LastfmStatus GetLfmStatus();
    }
}