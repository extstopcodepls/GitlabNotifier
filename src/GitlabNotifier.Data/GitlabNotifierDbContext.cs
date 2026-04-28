using GitlabNotifier.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GitlabNotifier.Data;

public sealed class GitlabNotifierDbContext(DbContextOptions<GitlabNotifierDbContext> options)
    : DbContext(options)
{
    public DbSet<GitlabUserEntity> GitlabUsers => Set<GitlabUserEntity>();

    public DbSet<MergeRequestEntity> MergeRequests => Set<MergeRequestEntity>();

    public DbSet<MergeRequestAssigneeEntity> MergeRequestAssignees => Set<MergeRequestAssigneeEntity>();

    public DbSet<MergeRequestReviewerEntity> MergeRequestReviewers => Set<MergeRequestReviewerEntity>();

    public DbSet<MergeRequestApprovalEntity> MergeRequestApprovals => Set<MergeRequestApprovalEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GitlabUserEntity>(entity =>
        {
            entity.ToTable("GitlabUsers");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Name).HasMaxLength(512);
            entity.Property(user => user.Username).HasMaxLength(255);
            entity.Property(user => user.WebUrl).HasMaxLength(2048);
        });

        modelBuilder.Entity<MergeRequestEntity>(entity =>
        {
            entity.ToTable("MergeRequests");
            entity.HasKey(mergeRequest => mergeRequest.Id);
            entity.HasIndex(mergeRequest => new { mergeRequest.ProjectId, mergeRequest.Iid })
                .IsUnique();
            entity.Property(mergeRequest => mergeRequest.Title).HasMaxLength(1024);
            entity.Property(mergeRequest => mergeRequest.WebUrl).HasMaxLength(2048);
            entity.HasOne(mergeRequest => mergeRequest.Author)
                .WithMany()
                .HasForeignKey(mergeRequest => mergeRequest.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MergeRequestAssigneeEntity>(entity =>
        {
            entity.ToTable("MergeRequestAssignees");
            entity.HasKey(assignee => new { assignee.MergeRequestId, assignee.UserId });
            entity.HasOne(assignee => assignee.MergeRequest)
                .WithMany(mergeRequest => mergeRequest.Assignees)
                .HasForeignKey(assignee => assignee.MergeRequestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(assignee => assignee.User)
                .WithMany()
                .HasForeignKey(assignee => assignee.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MergeRequestReviewerEntity>(entity =>
        {
            entity.ToTable("MergeRequestReviewers");
            entity.HasKey(reviewer => new { reviewer.MergeRequestId, reviewer.UserId });
            entity.HasOne(reviewer => reviewer.MergeRequest)
                .WithMany(mergeRequest => mergeRequest.Reviewers)
                .HasForeignKey(reviewer => reviewer.MergeRequestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(reviewer => reviewer.User)
                .WithMany()
                .HasForeignKey(reviewer => reviewer.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MergeRequestApprovalEntity>(entity =>
        {
            entity.ToTable("MergeRequestApprovals");
            entity.HasKey(approval => new { approval.MergeRequestId, approval.UserId });
            entity.HasOne(approval => approval.MergeRequest)
                .WithMany(mergeRequest => mergeRequest.Approvals)
                .HasForeignKey(approval => approval.MergeRequestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(approval => approval.User)
                .WithMany()
                .HasForeignKey(approval => approval.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
