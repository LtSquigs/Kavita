using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using API.Data.ManualMigrations;
using API.DTOs;
using API.DTOs.Progress;
using API.Entities;
using API.Entities.Enums;
using API.Extensions.QueryExtensions;
using API.Services.Tasks.Scanner.Parser;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;

namespace API.Data.Repositories;
#nullable enable

public interface IAppUserProgressRepository
{
    void Update(AppUserProgress userProgress);
    void Remove(AppUserProgress userProgress);
    Task<int> CleanupAbandonedChapters();
    Task<bool> UserHasProgress(LibraryType libraryType, int userId);
    Task<AppUserProgress?> GetUserProgressAsync(int chapterId, int userId);
    Task<bool> HasAnyProgressOnSeriesAsync(int seriesId, int userId);
    /// <summary>
    /// This is built exclusively for <see cref="MigrateUserProgressLibraryId"/>
    /// </summary>
    /// <returns></returns>
    Task<AppUserProgress?> GetAnyProgress();
    Task<IEnumerable<AppUserProgress>> GetUserProgressForSeriesAsync(int seriesId, int userId);
    Task<IEnumerable<AppUserProgress>> GetAllProgress();
    Task<DateTime> GetLatestProgress();
    Task<ProgressDto?> GetUserProgressDtoAsync(int chapterId, int userId);
    Task<bool> AnyUserProgressForSeriesAsync(int seriesId, int userId);
    Task<int> GetHighestFullyReadChapterForSeries(int seriesId, int userId);
    Task<float> GetHighestFullyReadVolumeForSeries(int seriesId, int userId);
    Task<DateTime?> GetLatestProgressForSeries(int seriesId, int userId);
    Task<DateTime?> GetFirstProgressForSeries(int seriesId, int userId);
    Task UpdateAllProgressThatAreMoreThanChapterPages();
    Task<IList<FullProgressDto>> GetUserProgressForChapter(int chapterId, int userId = 0);
    Task<IEnumerable<int>> GetUsersThatHaveFinishedVolume(Volume volume);
    Task UsersUpdateVolumesCompleted(IEnumerable<(Volume, int)> updates);
}

public class AppUserProgressRepository : IAppUserProgressRepository
{
    private readonly DataContext _context;
    private readonly IMapper _mapper;

    public AppUserProgressRepository(DataContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public void Update(AppUserProgress userProgress)
    {
        _context.Entry(userProgress).State = EntityState.Modified;
    }

    public void Remove(AppUserProgress userProgress)
    {
        _context.Remove(userProgress);
    }

    /// <summary>
    /// This will remove any entries that have chapterIds that no longer exists. This will execute the save as well.
    /// </summary>
    public async Task<int> CleanupAbandonedChapters()
    {
        var chapterIds = _context.Chapter.Select(c => c.Id);

        var rowsToRemove = await _context.AppUserProgresses
            .Where(progress => !chapterIds.Contains(progress.ChapterId))
            .ToListAsync();

        var rowsToRemoveBookmarks = await _context.AppUserBookmark
            .Where(progress => !chapterIds.Contains(progress.ChapterId))
            .ToListAsync();

        var rowsToRemoveReadingLists = await _context.ReadingListItem
            .Where(item => !chapterIds.Contains(item.ChapterId))
            .ToListAsync();

        _context.RemoveRange(rowsToRemove);
        _context.RemoveRange(rowsToRemoveBookmarks);
        _context.RemoveRange(rowsToRemoveReadingLists);
        return await _context.SaveChangesAsync() > 0 ? rowsToRemove.Count : 0;
    }

    /// <summary>
    /// Checks if user has any progress against a library of passed type
    /// </summary>
    /// <param name="libraryType"></param>
    /// <param name="userId"></param>
    /// <returns></returns>
    public async Task<bool> UserHasProgress(LibraryType libraryType, int userId)
    {
        var seriesIds = await _context.AppUserProgresses
            .Where(aup => aup.PagesRead > 0 && aup.AppUserId == userId)
            .AsNoTracking()
            .Select(aup => aup.SeriesId)
            .ToListAsync();

        if (seriesIds.Count == 0) return false;

        return await _context.Series
            .Include(s => s.Library)
            .Where(s => seriesIds.Contains(s.Id) && s.Library.Type == libraryType)
            .AsNoTracking()
            .AnyAsync();
    }

    public async Task<bool> HasAnyProgressOnSeriesAsync(int seriesId, int userId)
    {
        return await _context.AppUserProgresses
            .AnyAsync(aup => aup.PagesRead > 0 && aup.AppUserId == userId && aup.SeriesId == seriesId);
    }

    #nullable enable
    public async Task<AppUserProgress?> GetAnyProgress()
    {
        return await _context.AppUserProgresses.FirstOrDefaultAsync();
    }
    #nullable disable

    /// <summary>
    /// This will return any user progress. This filters out progress rows that have no pages read.
    /// </summary>
    /// <param name="seriesId"></param>
    /// <param name="userId"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AppUserProgress>> GetUserProgressForSeriesAsync(int seriesId, int userId)
    {
        return await _context.AppUserProgresses
            .Where(p => p.SeriesId == seriesId && p.AppUserId == userId && p.PagesRead > 0)
            .ToListAsync();
    }

    public async Task<IEnumerable<AppUserProgress>> GetAllProgress()
    {
        return await _context.AppUserProgresses.ToListAsync();
    }

    /// <summary>
    /// Returns the latest progress in UTC
    /// </summary>
    /// <returns></returns>
    public async Task<DateTime> GetLatestProgress()
    {
        return await _context.AppUserProgresses
            .Select(d => d.LastModifiedUtc)
            .OrderByDescending(d => d)
            .FirstOrDefaultAsync();
    }

    public async Task<ProgressDto?> GetUserProgressDtoAsync(int chapterId, int userId)
    {
        return await _context.AppUserProgresses
            .Where(p => p.AppUserId == userId && p.ChapterId == chapterId)
            .ProjectTo<ProgressDto>(_mapper.ConfigurationProvider)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> AnyUserProgressForSeriesAsync(int seriesId, int userId)
    {
        return await _context.AppUserProgresses
            .Where(p => p.SeriesId == seriesId && p.AppUserId == userId && p.PagesRead > 0)
            .AnyAsync();
    }

    public async Task<int> GetHighestFullyReadChapterForSeries(int seriesId, int userId)
    {
        var list = await _context.AppUserProgresses
            .Join(_context.Chapter, appUserProgresses => appUserProgresses.ChapterId, chapter => chapter.Id,
                (appUserProgresses, chapter) => new {appUserProgresses, chapter})
            .Where(p => p.appUserProgresses.SeriesId == seriesId && p.appUserProgresses.AppUserId == userId &&
                        p.appUserProgresses.PagesRead >= p.chapter.Pages)
            .Where(p => p.chapter.MaxNumber != Parser.SpecialVolumeNumber)
            .Select(p => p.chapter.MaxNumber)
            .ToListAsync();
        return list.Count == 0 ? 0 : (int) list.DefaultIfEmpty().Max(d => d);
    }

    public async Task<float> GetHighestFullyReadVolumeForSeries(int seriesId, int userId)
    {
        var list = await _context.AppUserProgresses
            .Join(_context.Chapter, appUserProgresses => appUserProgresses.ChapterId, chapter => chapter.Id,
                (appUserProgresses, chapter) => new {appUserProgresses, chapter})
            .Where(p => p.appUserProgresses.SeriesId == seriesId && p.appUserProgresses.AppUserId == userId &&
                        p.appUserProgresses.PagesRead >= p.chapter.Pages)
            .Where(p => p.chapter.MaxNumber != Parser.SpecialVolumeNumber)
            .Select(p => p.chapter.Volume.MaxNumber)
            .ToListAsync();

        return list.Count == 0 ? 0 : list.DefaultIfEmpty().Max();
    }

    public async Task<DateTime?> GetLatestProgressForSeries(int seriesId, int userId)
    {
        var list = await _context.AppUserProgresses.Where(p => p.AppUserId == userId && p.SeriesId == seriesId)
            .Select(p => p.LastModifiedUtc)
            .ToListAsync();
        return list.Count == 0 ? null : list.DefaultIfEmpty().Max();
    }

    public async Task<DateTime?> GetFirstProgressForSeries(int seriesId, int userId)
    {
        var list = await _context.AppUserProgresses.Where(p => p.AppUserId == userId && p.SeriesId == seriesId)
            .Select(p => p.LastModifiedUtc)
            .ToListAsync();
        return list.Count == 0 ? null : list.DefaultIfEmpty().Min();
    }

    public async Task UpdateAllProgressThatAreMoreThanChapterPages()
    {
        var updates = _context.AppUserProgresses
            .Join(_context.Chapter,
                progress => progress.ChapterId,
                chapter => chapter.Id,
                (progress, chapter) => new
                {
                    Progress = progress,
                    Chapter = chapter
                })
            .Where(joinResult => joinResult.Progress.PagesRead > joinResult.Chapter.Pages)
            .Select(result => new
            {
                ProgressId = result.Progress.Id,
                NewPagesRead = Math.Min(result.Progress.PagesRead, result.Chapter.Pages)
            })
            .AsEnumerable();

        // Need to run this Raw because DataContext will update LastModified on the entity which breaks ordering for progress
        var sqlBuilder = new StringBuilder();
        foreach (var update in updates)
        {
            sqlBuilder.Append($"UPDATE AppUserProgresses SET PagesRead = {update.NewPagesRead} WHERE Id = {update.ProgressId};");
        }

        // Execute the batch SQL
        var batchSql = sqlBuilder.ToString();
        await _context.Database.ExecuteSqlRawAsync(batchSql);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="chapterId"></param>
    /// <param name="userId">If 0, will pull all records</param>
    /// <returns></returns>
    public async Task<IList<FullProgressDto>> GetUserProgressForChapter(int chapterId, int userId = 0)
    {
        return await _context.AppUserProgresses
            .WhereIf(userId > 0, p => p.AppUserId == userId)
            .Where(p => p.ChapterId == chapterId)
            .Include(p => p.AppUser)
            .Select(p => new FullProgressDto()
            {
                AppUserId = p.AppUserId,
                ChapterId = p.ChapterId,
                PagesRead = p.PagesRead,
                Id = p.Id,
                Created = p.Created,
                CreatedUtc = p.CreatedUtc,
                LastModified = p.LastModified,
                LastModifiedUtc = p.LastModifiedUtc,
                UserName = p.AppUser.UserName
            })
            .ToListAsync();
    }

#nullable enable
    public async Task<AppUserProgress?> GetUserProgressAsync(int chapterId, int userId)
    {
        return await _context.AppUserProgresses
            .Where(p => p.ChapterId == chapterId && p.AppUserId == userId)
            .FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<int>> GetUsersThatHaveFinishedVolume(Volume volume)
    {
        if (volume == null || volume.Id == 0) return [];
        var numChapters = volume.Chapters.Count();
        return await _context.AppUserProgresses
            .Join(
                _context.Chapter,
                p => new { p.ChapterId, p.VolumeId },
                c => new { ChapterId = c.Id, c.VolumeId },
                (p, c) => new {
                    p.Id,
                    UserId = p.AppUserId,
                    p.ChapterId,
                    p.VolumeId,
                    ChapterVolumeId = c.VolumeId,
                    HasRead = p.PagesRead >= c.Pages, 
                }
            )
            .Where(p =>
                p.VolumeId == volume.Id
            )
            .GroupBy(p => p.UserId, (userId, ps) => new {
                    UserId = userId,
                    HasRead = ps.Count(p => p.HasRead) == numChapters
            })
            .Where((p) => p.HasRead)
            .Select((p) => p.UserId)
            .ToListAsync();
    }


    public async Task UsersUpdateVolumesCompleted(IEnumerable<(Volume, int)> updates) {
        var usersToUpdateByVolume = updates.GroupBy(u => u.Item1).Select(g => new { Volume = g.Key, UserIds = g.Select(u => u.Item2)});

        foreach(var update in usersToUpdateByVolume) {
            // First we update every row where a chapter already exists
            await _context.AppUserProgresses
                .Where(p => p.VolumeId == update.Volume.Id && update.UserIds.Contains(p.AppUserId))
                .Join(
                    _context.Chapter,
                    (u) => new { u.ChapterId, u.VolumeId },
                    (c) => new { ChapterId = c.Id, c.VolumeId },
                    (u, c) => new {
                        AppUserProgresses = u,
                        c.Pages,
                        ChapterVolumeId = c.VolumeId
                    }
                ).Select(p => new {
                    p.AppUserProgresses,
                    p.Pages
                }).ExecuteUpdateAsync(setters => setters.SetProperty(p => p.AppUserProgresses.PagesRead, p => p.Pages));

            // For new chapters the chapters will be in the database, but there will be no AppUserProgress
            var newChapters = await _context.Chapter
                .Where(c => c.VolumeId == update.Volume.Id)
                .GroupJoin(
                    _context.AppUserProgresses,
                    c => new { ChapterId = c.Id, c.VolumeId},
                    u => new { u.ChapterId, u.VolumeId },
                    (c, u) => new {
                        Chapter = c,
                        AppUserProgress = u
                    }
                )
                .SelectMany(x => x.AppUserProgress.DefaultIfEmpty(), (c, u) => new {
                    c.Chapter.Id,
                    c.Chapter.Pages,
                    NotExists = u == null
                })
                .Where(p => p.NotExists)
                .Select(c => new { c.Id, c.Pages })
                .ToListAsync();

            var newUpdates = newChapters.SelectMany((chapter) => {
                return update.UserIds.Select((userId) => {
                    return new AppUserProgress {
                        AppUserId = userId,
                        PagesRead = chapter.Pages,
                        ChapterId = chapter.Id,
                        VolumeId = update.Volume.Id,
                        SeriesId = update.Volume.SeriesId,
                        LibraryId = update.Volume.Series.LibraryId,
                        Created = DateTime.Now,
                        CreatedUtc = DateTime.UtcNow,
                        LastModified = DateTime.Now,
                        LastModifiedUtc = DateTime.UtcNow
                    };
                });
            });

            await _context.AppUserProgresses.AddRangeAsync(newUpdates);
        }

    }
}
