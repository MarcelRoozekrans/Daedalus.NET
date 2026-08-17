# Brainstorm Sessions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add interactive, phased brainstorming conversations that produce design docs, implementation plans, and phased tasks before Ralph Loop executes autonomously.

**Architecture:** New `BrainstormSession` aggregate root with 6-phase state machine. Chat-like API (`POST /messages`, `POST /advance`). Blazor WASM chat UI. Reuses existing `IRalphAgentFactory` for LLM calls and `ConvertPrdToTasksCommandHandler` for task creation.

**Tech Stack:** .NET 10, EF Core (PostgreSQL), Blazor WASM (Radzen), CSharpFunctionalExtensions `Result<T>`, FluentValidation, xUnit + NSubstitute + AwesomeAssertions

---

## Task 1: BrainstormPhase Enum

**Files:**
- Create: `src/Daedalus.Domain/Entities/BrainstormPhase.cs`
- Test: `tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormPhaseTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormPhaseTests.cs
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

public class BrainstormPhaseTests
{
    [Theory]
    [InlineData(BrainstormPhase.ContextGathering, 0)]
    [InlineData(BrainstormPhase.Clarification, 1)]
    [InlineData(BrainstormPhase.Proposals, 2)]
    [InlineData(BrainstormPhase.DesignReview, 3)]
    [InlineData(BrainstormPhase.PlanGeneration, 4)]
    [InlineData(BrainstormPhase.TaskCreation, 5)]
    [InlineData(BrainstormPhase.Completed, 6)]
    [InlineData(BrainstormPhase.Abandoned, 7)]
    public void BrainstormPhase_HasCorrectValues(BrainstormPhase phase, int expectedValue)
    {
        ((int)phase).Should().Be(expectedValue);
    }

    [Fact]
    public void BrainstormPhase_HasEightValues()
    {
        Enum.GetValues<BrainstormPhase>().Should().HaveCount(8);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Domain --filter "BrainstormPhaseTests" --no-build`
Expected: FAIL — `BrainstormPhase` type does not exist.

**Step 3: Write minimal implementation**

```csharp
// src/Daedalus.Domain/Entities/BrainstormPhase.cs
namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents the current phase of a brainstorming session.
/// </summary>
public enum BrainstormPhase
{
    ContextGathering = 0,
    Clarification = 1,
    Proposals = 2,
    DesignReview = 3,
    PlanGeneration = 4,
    TaskCreation = 5,
    Completed = 6,
    Abandoned = 7
}
```

**Step 4: Build and run test to verify it passes**

Run: `dotnet build src/Daedalus.Domain && dotnet test tests/Daedalus.Tests.Unit.Domain --filter "BrainstormPhaseTests"`
Expected: PASS (2 tests)

**Step 5: Commit**

```bash
git add src/Daedalus.Domain/Entities/BrainstormPhase.cs tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormPhaseTests.cs
git commit -m "feat: add BrainstormPhase enum with 8 phases"
```

---

## Task 2: BrainstormMessage Value Object

**Files:**
- Create: `src/Daedalus.Domain/Entities/BrainstormMessage.cs`
- Create: `src/Daedalus.Domain/Entities/MessageRole.cs`
- Test: `tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormMessageTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormMessageTests.cs
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

public class BrainstormMessageTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        var result = BrainstormMessage.Create(
            MessageRole.User,
            "Hello, I need help with a feature",
            BrainstormPhase.Clarification);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(MessageRole.User);
        result.Value.Content.Should().Be("Hello, I need help with a feature");
        result.Value.Phase.Should().Be(BrainstormPhase.Clarification);
        result.Value.Id.Should().NotBe(Guid.Empty);
        result.Value.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_WithEmptyContent_ShouldFail()
    {
        var result = BrainstormMessage.Create(
            MessageRole.User,
            "",
            BrainstormPhase.Clarification);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Content");
    }

    [Fact]
    public void Create_WithWhitespaceContent_ShouldFail()
    {
        var result = BrainstormMessage.Create(
            MessageRole.Assistant,
            "   ",
            BrainstormPhase.Proposals);

        result.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(MessageRole.System)]
    [InlineData(MessageRole.Assistant)]
    [InlineData(MessageRole.User)]
    public void Create_WithAllRoles_ShouldSucceed(MessageRole role)
    {
        var result = BrainstormMessage.Create(role, "Some content", BrainstormPhase.ContextGathering);
        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(role);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Domain --filter "BrainstormMessageTests" --no-build`
Expected: FAIL — types do not exist.

**Step 3: Write minimal implementation**

```csharp
// src/Daedalus.Domain/Entities/MessageRole.cs
namespace Daedalus.Domain.Entities;

/// <summary>
///     Role of a message in a brainstorming conversation.
/// </summary>
public enum MessageRole
{
    System = 0,
    Assistant = 1,
    User = 2
}
```

```csharp
// src/Daedalus.Domain/Entities/BrainstormMessage.cs
using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     A single message in a brainstorming conversation.
/// </summary>
public sealed class BrainstormMessage
{
    public Guid Id { get; private set; }
    public Guid BrainstormSessionId { get; private set; }
    public MessageRole Role { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public BrainstormPhase Phase { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private BrainstormMessage() { } // EF Core

    public static Result<BrainstormMessage> Create(
        MessageRole role,
        string content,
        BrainstormPhase phase)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Result.Failure<BrainstormMessage>("Content cannot be empty.");

        return Result.Success(new BrainstormMessage
        {
            Id = Guid.NewGuid(),
            Role = role,
            Content = content,
            Phase = phase,
            CreatedAt = DateTime.UtcNow
        });
    }
}
```

**Step 4: Build and run test to verify it passes**

Run: `dotnet build src/Daedalus.Domain && dotnet test tests/Daedalus.Tests.Unit.Domain --filter "BrainstormMessageTests"`
Expected: PASS (4 tests)

**Step 5: Commit**

```bash
git add src/Daedalus.Domain/Entities/BrainstormMessage.cs src/Daedalus.Domain/Entities/MessageRole.cs tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormMessageTests.cs
git commit -m "feat: add BrainstormMessage value object with MessageRole enum"
```

---

## Task 3: BrainstormSession Aggregate Root

**Files:**
- Create: `src/Daedalus.Domain/Entities/BrainstormSession.cs`
- Test: `tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormSessionTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormSessionTests.cs
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

public class BrainstormSessionTests
{
    private readonly Guid _projectId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidProjectId_ShouldSucceed()
    {
        var result = BrainstormSession.Create(_projectId);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProjectId.Should().Be(_projectId);
        result.Value.Phase.Should().Be(BrainstormPhase.ContextGathering);
        result.Value.Messages.Should().BeEmpty();
        result.Value.DesignDocument.Should().BeNull();
        result.Value.ImplementationPlan.Should().BeNull();
        result.Value.PhaseCompleteSignaled.Should().BeFalse();
        result.Value.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Create_WithEmptyProjectId_ShouldFail()
    {
        var result = BrainstormSession.Create(Guid.Empty);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project");
    }

    [Fact]
    public void AddMessage_WithValidContent_ShouldAppendToMessages()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        var result = session.AddMessage(MessageRole.Assistant, "Welcome to brainstorming!");

        result.IsSuccess.Should().BeTrue();
        session.Messages.Should().HaveCount(1);
        session.Messages[0].Role.Should().Be(MessageRole.Assistant);
        session.Messages[0].Phase.Should().Be(BrainstormPhase.ContextGathering);
    }

    [Fact]
    public void AddMessage_WithPhaseCompleteMarker_ShouldSetFlag()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        var result = session.AddMessage(MessageRole.Assistant, "Context gathered. [PHASE_COMPLETE]");

        result.IsSuccess.Should().BeTrue();
        session.PhaseCompleteSignaled.Should().BeTrue();
        // Marker should be stripped from stored content
        session.Messages[0].Content.Should().NotContain("[PHASE_COMPLETE]");
    }

    [Fact]
    public void AdvancePhase_WhenSignaled_ShouldMoveForward()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");

        var result = session.AdvancePhase();

        result.IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(BrainstormPhase.Clarification);
        session.PhaseCompleteSignaled.Should().BeFalse();
    }

    [Fact]
    public void AdvancePhase_WhenNotSignaled_ShouldFail()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        var result = session.AdvancePhase();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not signaled");
    }

    [Fact]
    public void AdvancePhase_FromTaskCreation_ShouldMoveToCompleted()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        // Advance through all phases
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        session.Phase.Should().Be(BrainstormPhase.TaskCreation);
        session.AddMessage(MessageRole.Assistant, "Tasks ready. [PHASE_COMPLETE]");
        var result = session.AdvancePhase();

        result.IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(BrainstormPhase.Completed);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void AdvancePhase_WhenCompleted_ShouldFail()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        for (var i = 0; i < 6; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        session.Phase.Should().Be(BrainstormPhase.Completed);
        var result = session.AdvancePhase();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("terminal");
    }

    [Fact]
    public void Abandon_ShouldSetPhaseToAbandoned()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        var result = session.Abandon();

        result.IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(BrainstormPhase.Abandoned);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Abandon_WhenAlreadyCompleted_ShouldFail()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        for (var i = 0; i < 6; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        var result = session.Abandon();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void SetDesignDocument_ShouldStoreMarkdown()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        var design = "# Architecture\n\nUse clean architecture.";

        var result = session.SetDesignDocument(design);

        result.IsSuccess.Should().BeTrue();
        session.DesignDocument.Should().Be(design);
    }

    [Fact]
    public void SetImplementationPlan_ShouldStoreMarkdown()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        var plan = "## Task 1\n\nCreate the entity.";

        var result = session.SetImplementationPlan(plan);

        result.IsSuccess.Should().BeTrue();
        session.ImplementationPlan.Should().Be(plan);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Domain --filter "BrainstormSessionTests" --no-build`
Expected: FAIL — `BrainstormSession` type does not exist.

**Step 3: Write minimal implementation**

```csharp
// src/Daedalus.Domain/Entities/BrainstormSession.cs
#pragma warning disable CA1819 // EF Core concurrency token standard pattern
#pragma warning disable S1144 // EF Core sets RowVersion via reflection

using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents an interactive brainstorming session that guides a user
///     through structured phases to produce a design document, implementation
///     plan, and phased tasks for autonomous Ralph Loop execution.
/// </summary>
public sealed class BrainstormSession : AggregateRoot<Guid>
{
    private const string PhaseCompleteMarker = "[PHASE_COMPLETE]";
    private readonly List<BrainstormMessage> _messages = [];

    public Guid ProjectId { get; private set; }
    public BrainstormPhase Phase { get; private set; } = BrainstormPhase.ContextGathering;
    public IReadOnlyList<BrainstormMessage> Messages => _messages.AsReadOnly();
    public string? DesignDocument { get; private set; }
    public string? ImplementationPlan { get; private set; }
    public bool PhaseCompleteSignaled { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public byte[]? RowVersion { get; private set; }

    private BrainstormSession() { } // EF Core

    public static Result<BrainstormSession> Create(Guid projectId)
    {
        if (projectId == Guid.Empty)
            return Result.Failure<BrainstormSession>("Project ID is required.");

        return Result.Success(new BrainstormSession
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Phase = BrainstormPhase.ContextGathering,
            CreatedAt = DateTime.UtcNow
        });
    }

    public Result AddMessage(MessageRole role, string content)
    {
        if (Phase is BrainstormPhase.Completed or BrainstormPhase.Abandoned)
            return Result.Failure("Cannot add messages to a session in a terminal state.");

        // Detect and strip phase complete marker
        var hasMarker = content.Contains(PhaseCompleteMarker, StringComparison.Ordinal);
        var storedContent = hasMarker
            ? content.Replace(PhaseCompleteMarker, "", StringComparison.Ordinal).TrimEnd()
            : content;

        var messageResult = BrainstormMessage.Create(role, storedContent, Phase);
        if (messageResult.IsFailure)
            return Result.Failure(messageResult.Error);

        _messages.Add(messageResult.Value);

        if (hasMarker)
            PhaseCompleteSignaled = true;

        return Result.Success();
    }

    public Result AdvancePhase()
    {
        if (Phase is BrainstormPhase.Completed or BrainstormPhase.Abandoned)
            return Result.Failure("Cannot advance a session in a terminal state.");

        if (!PhaseCompleteSignaled)
            return Result.Failure("Phase completion has not been signaled by the LLM.");

        Phase = Phase switch
        {
            BrainstormPhase.ContextGathering => BrainstormPhase.Clarification,
            BrainstormPhase.Clarification => BrainstormPhase.Proposals,
            BrainstormPhase.Proposals => BrainstormPhase.DesignReview,
            BrainstormPhase.DesignReview => BrainstormPhase.PlanGeneration,
            BrainstormPhase.PlanGeneration => BrainstormPhase.TaskCreation,
            BrainstormPhase.TaskCreation => BrainstormPhase.Completed,
            _ => throw new InvalidOperationException($"Unexpected phase: {Phase}")
        };

        PhaseCompleteSignaled = false;

        if (Phase == BrainstormPhase.Completed)
            CompletedAt = DateTime.UtcNow;

        return Result.Success();
    }

    public Result Abandon()
    {
        if (Phase is BrainstormPhase.Completed or BrainstormPhase.Abandoned)
            return Result.Failure("Cannot abandon a session in a terminal state.");

        Phase = BrainstormPhase.Abandoned;
        CompletedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result SetDesignDocument(string designDocument)
    {
        if (string.IsNullOrWhiteSpace(designDocument))
            return Result.Failure("Design document cannot be empty.");

        DesignDocument = designDocument;
        return Result.Success();
    }

    public Result SetImplementationPlan(string plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return Result.Failure("Implementation plan cannot be empty.");

        ImplementationPlan = plan;
        return Result.Success();
    }
}
```

**Step 4: Build and run tests**

Run: `dotnet build src/Daedalus.Domain && dotnet test tests/Daedalus.Tests.Unit.Domain --filter "BrainstormSessionTests"`
Expected: PASS (11 tests)

**Step 5: Commit**

```bash
git add src/Daedalus.Domain/Entities/BrainstormSession.cs tests/Daedalus.Tests.Unit.Domain/Entities/BrainstormSessionTests.cs
git commit -m "feat: add BrainstormSession aggregate root with phase state machine"
```

---

## Task 4: EF Core Configuration and Migration

**Files:**
- Modify: `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs` — add DbSets
- Create: `src/Daedalus.Infrastructure/Persistence/Configurations/BrainstormSessionConfiguration.cs`
- Create: `src/Daedalus.Infrastructure/Persistence/Configurations/BrainstormMessageConfiguration.cs`
- Create: Migration via `dotnet ef` CLI

**Step 1: Add DbSets to ApplicationDbContext**

Add to the existing DbSet section in `ApplicationDbContext.cs`:

```csharp
public DbSet<BrainstormSession> BrainstormSessions => Set<BrainstormSession>();
public DbSet<BrainstormMessage> BrainstormMessages => Set<BrainstormMessage>();
```

**Step 2: Create EF Core entity configurations**

```csharp
// src/Daedalus.Infrastructure/Persistence/Configurations/BrainstormSessionConfiguration.cs
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

public sealed class BrainstormSessionConfiguration : IEntityTypeConfiguration<BrainstormSession>
{
    public void Configure(EntityTypeBuilder<BrainstormSession> builder)
    {
        builder.ToTable("BrainstormSessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ProjectId).IsRequired();

        builder.Property(s => s.Phase)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(s => s.DesignDocument)
            .HasColumnType("text");

        builder.Property(s => s.ImplementationPlan)
            .HasColumnType("text");

        builder.Property(s => s.PhaseCompleteSignaled)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.CompletedAt);

        builder.Property(s => s.RowVersion)
            .IsRowVersion();

        builder.HasMany(s => s.Messages)
            .WithOne()
            .HasForeignKey(m => m.BrainstormSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.ProjectId);
        builder.HasIndex(s => s.Phase);
    }
}
```

```csharp
// src/Daedalus.Infrastructure/Persistence/Configurations/BrainstormMessageConfiguration.cs
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

public sealed class BrainstormMessageConfiguration : IEntityTypeConfiguration<BrainstormMessage>
{
    public void Configure(EntityTypeBuilder<BrainstormMessage> builder)
    {
        builder.ToTable("BrainstormMessages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.BrainstormSessionId).IsRequired();

        builder.Property(m => m.Role)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(m => m.Content)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(m => m.Phase)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasIndex(m => m.BrainstormSessionId);
    }
}
```

**Step 3: Generate migration**

Run: `dotnet ef migrations add AddBrainstormSessions --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api`
Expected: Migration file created in `src/Daedalus.Infrastructure/Persistence/Migrations/`

**Step 4: Verify build**

Run: `dotnet build`
Expected: 0 errors

**Step 5: Commit**

```bash
git add src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs src/Daedalus.Infrastructure/Persistence/Configurations/BrainstormSessionConfiguration.cs src/Daedalus.Infrastructure/Persistence/Configurations/BrainstormMessageConfiguration.cs src/Daedalus.Infrastructure/Persistence/Migrations/
git commit -m "feat: add EF Core configuration and migration for BrainstormSession"
```

---

## Task 5: Repository Interface and Implementation

**Files:**
- Create: `src/Daedalus.Application/Abstractions/IBrainstormRepository.cs`
- Create: `src/Daedalus.Infrastructure/Persistence/Repositories/BrainstormRepository.cs`
- Test: `tests/Daedalus.Tests.Unit.Infrastructure/Persistence/BrainstormRepositoryTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Daedalus.Tests.Unit.Infrastructure/Persistence/BrainstormRepositoryTests.cs
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Tests.Unit.Infrastructure.Persistence;

public class BrainstormRepositoryTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IBrainstormRepository _repository;

    public BrainstormRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);
        _repository = new BrainstormRepository(_dbContext);
    }

    [Fact]
    public async Task AddAsync_WithValidSession_ShouldPersist()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;

        var result = await _repository.AddAsync(session, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stored = await _dbContext.BrainstormSessions.FindAsync(session.Id);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_WithExistingSession_ShouldReturnWithMessages()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.AddMessage(MessageRole.Assistant, "Hello!");
        _dbContext.BrainstormSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetByIdAsync(session.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Messages.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistentId_ShouldReturnFailure()
    {
        var result = await _repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetByProjectIdAsync_ShouldReturnSessionsForProject()
    {
        var projectId = Guid.NewGuid();
        var session1 = BrainstormSession.Create(projectId).Value;
        var session2 = BrainstormSession.Create(projectId).Value;
        var otherSession = BrainstormSession.Create(Guid.NewGuid()).Value;
        _dbContext.BrainstormSessions.AddRange(session1, session2, otherSession);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetByProjectIdAsync(projectId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateAsync_ShouldPersistChanges()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _dbContext.BrainstormSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        session.AddMessage(MessageRole.User, "I need a feature");
        var result = await _repository.UpdateAsync(session, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Infrastructure --filter "BrainstormRepositoryTests" --no-build`
Expected: FAIL — `IBrainstormRepository` does not exist.

**Step 3: Write minimal implementation**

```csharp
// src/Daedalus.Application/Abstractions/IBrainstormRepository.cs
using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for brainstorming session persistence.
/// </summary>
public interface IBrainstormRepository
{
    Task<Result<BrainstormSession>> AddAsync(BrainstormSession session, CancellationToken ct);
    Task<Result<BrainstormSession>> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Result<IReadOnlyList<BrainstormSession>>> GetByProjectIdAsync(Guid projectId, CancellationToken ct);
    Task<Result> UpdateAsync(BrainstormSession session, CancellationToken ct);
}
```

```csharp
// src/Daedalus.Infrastructure/Persistence/Repositories/BrainstormRepository.cs
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Infrastructure.Persistence.Repositories;

/// <summary>
///     EF Core implementation of brainstorming session repository.
/// </summary>
public sealed class BrainstormRepository(ApplicationDbContext dbContext) : IBrainstormRepository
{
    public async Task<Result<BrainstormSession>> AddAsync(BrainstormSession session, CancellationToken ct)
    {
        try
        {
            dbContext.BrainstormSessions.Add(session);
            await dbContext.SaveChangesAsync(ct);
            return Result.Success(session);
        }
        catch (Exception ex)
        {
            return Result.Failure<BrainstormSession>($"Failed to add brainstorm session: {ex.Message}");
        }
    }

    public async Task<Result<BrainstormSession>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var session = await dbContext.BrainstormSessions
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        return session is not null
            ? Result.Success(session)
            : Result.Failure<BrainstormSession>($"Brainstorm session {id} not found.");
    }

    public async Task<Result<IReadOnlyList<BrainstormSession>>> GetByProjectIdAsync(
        Guid projectId, CancellationToken ct)
    {
        var sessions = await dbContext.BrainstormSessions
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<BrainstormSession>>(sessions);
    }

    public async Task<Result> UpdateAsync(BrainstormSession session, CancellationToken ct)
    {
        try
        {
            dbContext.BrainstormSessions.Update(session);
            await dbContext.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update brainstorm session: {ex.Message}");
        }
    }
}
```

**Step 4: Build and run tests**

Run: `dotnet build && dotnet test tests/Daedalus.Tests.Unit.Infrastructure --filter "BrainstormRepositoryTests"`
Expected: PASS (5 tests)

**Step 5: Commit**

```bash
git add src/Daedalus.Application/Abstractions/IBrainstormRepository.cs src/Daedalus.Infrastructure/Persistence/Repositories/BrainstormRepository.cs tests/Daedalus.Tests.Unit.Infrastructure/Persistence/BrainstormRepositoryTests.cs
git commit -m "feat: add IBrainstormRepository interface and EF Core implementation"
```

---

## Task 6: Phase-Specific System Prompts

**Files:**
- Create: `src/Daedalus.Application/Services/Brainstorm/BrainstormPromptTemplates.cs`
- Test: `tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormPromptTemplatesTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormPromptTemplatesTests.cs
using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Services.Brainstorm;

public class BrainstormPromptTemplatesTests
{
    [Theory]
    [InlineData(BrainstormPhase.ContextGathering)]
    [InlineData(BrainstormPhase.Clarification)]
    [InlineData(BrainstormPhase.Proposals)]
    [InlineData(BrainstormPhase.DesignReview)]
    [InlineData(BrainstormPhase.PlanGeneration)]
    public void GetSystemPrompt_ForEachPhase_ShouldReturnNonEmptyPrompt(BrainstormPhase phase)
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(phase);

        prompt.Should().NotBeNullOrWhiteSpace();
        prompt.Should().Contain("[PHASE_COMPLETE]");
    }

    [Fact]
    public void GetSystemPrompt_ForContextGathering_ShouldContainProjectPlaceholder()
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.ContextGathering);
        prompt.Should().Contain("{0}"); // project context placeholder
    }

    [Fact]
    public void GetSystemPrompt_ForClarification_ShouldMentionOneQuestion()
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.Clarification);
        prompt.Should().Contain("ONE question");
    }

    [Fact]
    public void GetSystemPrompt_ForProposals_ShouldMention2To3Approaches()
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.Proposals);
        prompt.Should().Contain("2-3");
    }

    [Fact]
    public void GetSystemPrompt_ForPlanGeneration_ShouldMentionTDD()
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.PlanGeneration);
        prompt.Should().Contain("TDD");
    }

    [Theory]
    [InlineData(BrainstormPhase.TaskCreation)]
    [InlineData(BrainstormPhase.Completed)]
    [InlineData(BrainstormPhase.Abandoned)]
    public void GetSystemPrompt_ForTerminalPhases_ShouldReturnEmpty(BrainstormPhase phase)
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(phase);
        prompt.Should().BeEmpty();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Application --filter "BrainstormPromptTemplatesTests" --no-build`
Expected: FAIL — type does not exist.

**Step 3: Write minimal implementation**

```csharp
// src/Daedalus.Application/Services/Brainstorm/BrainstormPromptTemplates.cs
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Services.Brainstorm;

/// <summary>
///     Phase-specific system prompts for brainstorming conversations.
///     Each prompt guides the LLM's behavior during that phase.
/// </summary>
public static class BrainstormPromptTemplates
{
    public static string GetSystemPrompt(BrainstormPhase phase) => phase switch
    {
        BrainstormPhase.ContextGathering => """
            You are starting a brainstorming session. Here is the project context:

            {0}

            Summarize what you understand about this project in a structured way:
            - Project purpose and tech stack
            - Existing architecture patterns
            - Key learnings from past executions

            Identify any gaps in your understanding that need clarification.
            End your message with [PHASE_COMPLETE] when your summary is complete.
            """,

        BrainstormPhase.Clarification => """
            You are gathering requirements from the user. Follow these rules strictly:
            - Ask ONE question at a time
            - Prefer multiple-choice questions (2-4 options) when possible
            - Focus on: purpose, constraints, success criteria, scope boundaries
            - Do NOT propose solutions or implementation details yet
            - Keep questions concise and specific

            When you have enough information to propose implementation approaches,
            end your message with [PHASE_COMPLETE].
            """,

        BrainstormPhase.Proposals => """
            Based on the conversation so far, propose 2-3 implementation approaches.
            For each approach include:
            - A short descriptive name
            - A 2-3 sentence description of the approach
            - Trade-offs: pros and cons
            - Your recommendation with clear reasoning

            Lead with your recommended approach and explain why.
            End your message with [PHASE_COMPLETE] after presenting all approaches.
            """,

        BrainstormPhase.DesignReview => """
            The user has chosen an approach. Present the design in sections.
            Cover these areas (one section at a time):
            - Architecture overview
            - Key components and their responsibilities
            - Data flow and interactions
            - Error handling strategy
            - Testing strategy

            Present ONE section at a time. After each section, ask if it looks right
            before moving to the next. When all sections are approved,
            end your message with [PHASE_COMPLETE].
            """,

        BrainstormPhase.PlanGeneration => """
            Generate a detailed implementation plan from the approved design.
            Break it into bite-sized tasks following TDD principles:
            - Write failing test first
            - Implement minimal code to pass
            - Refactor if needed
            - Commit after each task

            For each task include:
            - Exact file paths (create/modify)
            - Complete code (not pseudocode)
            - Test commands with expected output
            - Phase label (e.g., "Backend", "Frontend", "Integration")
            - Parallel group number (tasks in same group can run in parallel)
            - Dependencies on other task IDs

            Output the plan as structured markdown.
            End your message with [PHASE_COMPLETE] when the plan is complete.
            """,

        _ => string.Empty
    };
}
```

**Step 4: Build and run tests**

Run: `dotnet build && dotnet test tests/Daedalus.Tests.Unit.Application --filter "BrainstormPromptTemplatesTests"`
Expected: PASS (8 tests)

**Step 5: Commit**

```bash
git add src/Daedalus.Application/Services/Brainstorm/BrainstormPromptTemplates.cs tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormPromptTemplatesTests.cs
git commit -m "feat: add phase-specific system prompt templates for brainstorming"
```

---

## Task 7: IBrainstormService Interface and Implementation

**Files:**
- Create: `src/Daedalus.Application/Abstractions/IBrainstormService.cs`
- Create: `src/Daedalus.Application/Services/Brainstorm/BrainstormService.cs`
- Test: `tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormServiceTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormServiceTests.cs
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services.Brainstorm;

public class BrainstormServiceTests
{
    private readonly IBrainstormRepository _repository = Substitute.For<IBrainstormRepository>();
    private readonly IRalphAgentFactory _agentFactory = Substitute.For<IRalphAgentFactory>();
    private readonly IProjectRepository _projectRepository = Substitute.For<IProjectRepository>();
    private readonly ILogger<BrainstormService> _logger = Substitute.For<ILogger<BrainstormService>>();
    private readonly BrainstormService _service;

    public BrainstormServiceTests()
    {
        _service = new BrainstormService(_repository, _agentFactory, _projectRepository, _logger);
    }

    [Fact]
    public async Task CreateSessionAsync_WithValidProject_ShouldCreateAndReturnSession()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = DomainTestFactory.CreateProject(projectId);
        _projectRepository.GetByIdAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(project));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult("Project summary here. [PHASE_COMPLETE]", 100, 200, "claude")));

        _repository.AddAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result.Success(callInfo.Arg<BrainstormSession>()));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.CreateSessionAsync(projectId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Phase.Should().Be(BrainstormPhase.ContextGathering);
        result.Value.Messages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateSessionAsync_WithInvalidProject_ShouldReturnFailure()
    {
        // Arrange
        _projectRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Daedalus.Domain.Entities.Project>("Project not found"));

        // Act
        var result = await _service.CreateSessionAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldCallLlmAndStoreResponse()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.AddMessage(MessageRole.Assistant, "Context gathered. [PHASE_COMPLETE]");
        session.AdvancePhase(); // Now in Clarification

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult("What type of feature?", 100, 200, "claude")));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.SendMessageAsync(session.Id, "I need a caching layer", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(MessageRole.Assistant);
        result.Value.Content.Should().Be("What type of feature?");
    }

    [Fact]
    public async Task AdvancePhaseAsync_WhenSignaled_ShouldAdvanceAndCallLlm()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult("First question?", 100, 200, "claude")));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.AdvancePhaseAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Phase.Should().Be(BrainstormPhase.Clarification);
    }

    [Fact]
    public async Task AbandonSessionAsync_ShouldMarkAsAbandoned()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.AbandonSessionAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Application --filter "BrainstormServiceTests" --no-build`
Expected: FAIL — types do not exist.

**Step 3: Write minimal implementation**

```csharp
// src/Daedalus.Application/Abstractions/IBrainstormService.cs
using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Service for managing interactive brainstorming sessions.
/// </summary>
public interface IBrainstormService
{
    Task<Result<BrainstormSession>> CreateSessionAsync(Guid projectId, CancellationToken ct);
    Task<Result<BrainstormSession>> GetSessionAsync(Guid sessionId, CancellationToken ct);
    Task<Result<IReadOnlyList<BrainstormSession>>> GetSessionsByProjectAsync(Guid projectId, CancellationToken ct);
    Task<Result<BrainstormMessage>> SendMessageAsync(Guid sessionId, string userMessage, CancellationToken ct);
    Task<Result<BrainstormSession>> AdvancePhaseAsync(Guid sessionId, CancellationToken ct);
    Task<Result> AbandonSessionAsync(Guid sessionId, CancellationToken ct);
}
```

```csharp
// src/Daedalus.Application/Services/Brainstorm/BrainstormService.cs
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Brainstorm;

/// <summary>
///     Orchestrates brainstorming sessions: manages conversation state,
///     calls LLM with phase-specific prompts, and handles phase transitions.
/// </summary>
public sealed partial class BrainstormService(
    IBrainstormRepository repository,
    IRalphAgentFactory agentFactory,
    IProjectRepository projectRepository,
    ILogger<BrainstormService> logger) : IBrainstormService
{
    public async Task<Result<BrainstormSession>> CreateSessionAsync(Guid projectId, CancellationToken ct)
    {
        // Validate project exists
        var projectResult = await projectRepository.GetByIdAsync(projectId, ct);
        if (projectResult.IsFailure)
            return Result.Failure<BrainstormSession>($"Project not found: {projectResult.Error}");

        var sessionResult = BrainstormSession.Create(projectId);
        if (sessionResult.IsFailure)
            return sessionResult;

        var session = sessionResult.Value;

        // Persist the session first
        var addResult = await repository.AddAsync(session, ct);
        if (addResult.IsFailure)
            return addResult;

        // Build context and invoke LLM for initial context gathering
        var project = projectResult.Value;
        var contextPrompt = string.Format(
            BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.ContextGathering),
            $"Project: {project.Name}\nDescription: {project.Description}\nVersion: {project.Version}");

        var llmResult = await agentFactory.InvokeAsync(contextPrompt, ct);
        if (llmResult.IsFailure)
            return Result.Failure<BrainstormSession>($"LLM invocation failed: {llmResult.Error}");

        session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
        await repository.UpdateAsync(session, ct);

        LogSessionCreated(logger, session.Id, projectId);
        return Result.Success(session);
    }

    public async Task<Result<BrainstormSession>> GetSessionAsync(Guid sessionId, CancellationToken ct)
    {
        return await repository.GetByIdAsync(sessionId, ct);
    }

    public async Task<Result<IReadOnlyList<BrainstormSession>>> GetSessionsByProjectAsync(
        Guid projectId, CancellationToken ct)
    {
        return await repository.GetByProjectIdAsync(projectId, ct);
    }

    public async Task<Result<BrainstormMessage>> SendMessageAsync(
        Guid sessionId, string userMessage, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct);
        if (sessionResult.IsFailure)
            return Result.Failure<BrainstormMessage>(sessionResult.Error);

        var session = sessionResult.Value;

        // Add user message
        var addResult = session.AddMessage(MessageRole.User, userMessage);
        if (addResult.IsFailure)
            return Result.Failure<BrainstormMessage>(addResult.Error);

        // Build conversation history for LLM
        var prompt = BuildConversationPrompt(session);

        // Call LLM
        var llmResult = await agentFactory.InvokeAsync(prompt, ct);
        if (llmResult.IsFailure)
            return Result.Failure<BrainstormMessage>($"LLM invocation failed: {llmResult.Error}");

        // Add assistant response
        session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
        await repository.UpdateAsync(session, ct);

        // Return the last assistant message
        var lastMessage = session.Messages[^1];
        return Result.Success(lastMessage);
    }

    public async Task<Result<BrainstormSession>> AdvancePhaseAsync(Guid sessionId, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct);
        if (sessionResult.IsFailure)
            return sessionResult;

        var session = sessionResult.Value;
        var advanceResult = session.AdvancePhase();
        if (advanceResult.IsFailure)
            return Result.Failure<BrainstormSession>(advanceResult.Error);

        // If the new phase has a system prompt, invoke LLM to start the phase
        var systemPrompt = BrainstormPromptTemplates.GetSystemPrompt(session.Phase);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            var prompt = BuildConversationPrompt(session);
            var llmResult = await agentFactory.InvokeAsync(prompt, ct);
            if (llmResult.IsSuccess)
            {
                session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
            }
        }

        await repository.UpdateAsync(session, ct);

        LogPhaseAdvanced(logger, sessionId, session.Phase);
        return Result.Success(session);
    }

    public async Task<Result> AbandonSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct);
        if (sessionResult.IsFailure)
            return Result.Failure(sessionResult.Error);

        var session = sessionResult.Value;
        var abandonResult = session.Abandon();
        if (abandonResult.IsFailure)
            return abandonResult;

        await repository.UpdateAsync(session, ct);

        LogSessionAbandoned(logger, sessionId);
        return Result.Success();
    }

    private static string BuildConversationPrompt(BrainstormSession session)
    {
        var sb = new StringBuilder();

        // Add phase-specific system prompt
        var systemPrompt = BrainstormPromptTemplates.GetSystemPrompt(session.Phase);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            sb.AppendLine(systemPrompt);
            sb.AppendLine();
        }

        // Add conversation history
        sb.AppendLine("=== CONVERSATION HISTORY ===");
        foreach (var message in session.Messages)
        {
            var roleLabel = message.Role switch
            {
                MessageRole.System => "SYSTEM",
                MessageRole.Assistant => "ASSISTANT",
                MessageRole.User => "USER",
                _ => "UNKNOWN"
            };
            sb.AppendLine($"[{roleLabel}]: {message.Content}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information,
        Message = "Brainstorm session {SessionId} created for project {ProjectId}")]
    private static partial void LogSessionCreated(ILogger logger, Guid sessionId, Guid projectId);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information,
        Message = "Brainstorm session {SessionId} advanced to phase {Phase}")]
    private static partial void LogPhaseAdvanced(ILogger logger, Guid sessionId, BrainstormPhase phase);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information,
        Message = "Brainstorm session {SessionId} abandoned")]
    private static partial void LogSessionAbandoned(ILogger logger, Guid sessionId);
}
```

**Step 4: Build and run tests**

Run: `dotnet build && dotnet test tests/Daedalus.Tests.Unit.Application --filter "BrainstormServiceTests"`
Expected: PASS (5 tests)

**Step 5: Commit**

```bash
git add src/Daedalus.Application/Abstractions/IBrainstormService.cs src/Daedalus.Application/Services/Brainstorm/BrainstormService.cs tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormServiceTests.cs
git commit -m "feat: add BrainstormService with LLM-driven conversation orchestration"
```

---

## Task 8: DTOs and Validator

**Files:**
- Create: `src/Daedalus.Application/DTOs/BrainstormDtos.cs`
- Create: `src/Daedalus.Application/Validators/SendBrainstormMessageDtoValidator.cs`
- Create: `src/Daedalus.Application/Mappers/BrainstormDtoMapper.cs`
- Test: `tests/Daedalus.Tests.Unit.Application/Validators/SendBrainstormMessageDtoValidatorTests.cs`

**Step 1: Create DTOs (no test needed — simple records)**

```csharp
// src/Daedalus.Application/DTOs/BrainstormDtos.cs
using Daedalus.Domain.Entities;

namespace Daedalus.Application.DTOs;

public record CreateBrainstormSessionDto(Guid ProjectId);

public record SendBrainstormMessageDto(string Content);

public record BrainstormSessionDto(
    Guid Id,
    Guid ProjectId,
    BrainstormPhase Phase,
    IReadOnlyList<BrainstormMessageDto> Messages,
    string? DesignDocument,
    string? ImplementationPlan,
    bool PhaseCompleteSignaled,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public record BrainstormMessageDto(
    Guid Id,
    string Role,
    string Content,
    BrainstormPhase Phase,
    DateTime CreatedAt);

public record BrainstormSessionSummaryDto(
    Guid Id,
    Guid ProjectId,
    BrainstormPhase Phase,
    int MessageCount,
    DateTime CreatedAt,
    DateTime? CompletedAt);
```

**Step 2: Create mapper**

```csharp
// src/Daedalus.Application/Mappers/BrainstormDtoMapper.cs
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Mappers;

public static class BrainstormDtoMapper
{
    public static BrainstormSessionDto ToDto(BrainstormSession session) => new(
        session.Id,
        session.ProjectId,
        session.Phase,
        session.Messages.Select(ToDto).ToList(),
        session.DesignDocument,
        session.ImplementationPlan,
        session.PhaseCompleteSignaled,
        session.CreatedAt,
        session.CompletedAt);

    public static BrainstormMessageDto ToDto(BrainstormMessage message) => new(
        message.Id,
        message.Role.ToString(),
        message.Content,
        message.Phase,
        message.CreatedAt);

    public static BrainstormSessionSummaryDto ToSummaryDto(BrainstormSession session) => new(
        session.Id,
        session.ProjectId,
        session.Phase,
        session.Messages.Count,
        session.CreatedAt,
        session.CompletedAt);
}
```

**Step 3: Write failing validator test**

```csharp
// tests/Daedalus.Tests.Unit.Application/Validators/SendBrainstormMessageDtoValidatorTests.cs
using Daedalus.Application.DTOs;
using Daedalus.Application.Validators;
using FluentValidation.TestHelper;

namespace Daedalus.Tests.Unit.Application.Validators;

public class SendBrainstormMessageDtoValidatorTests
{
    private readonly SendBrainstormMessageDtoValidator _validator = new();

    [Fact]
    public void Validate_WithValidContent_ShouldPass()
    {
        var dto = new SendBrainstormMessageDto("I need a caching layer");
        var result = _validator.TestValidate(dto);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithEmptyContent_ShouldFail()
    {
        var dto = new SendBrainstormMessageDto("");
        var result = _validator.TestValidate(dto);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }

    [Fact]
    public void Validate_WithTooLongContent_ShouldFail()
    {
        var dto = new SendBrainstormMessageDto(new string('x', 10001));
        var result = _validator.TestValidate(dto);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }
}
```

**Step 4: Write validator implementation**

```csharp
// src/Daedalus.Application/Validators/SendBrainstormMessageDtoValidator.cs
using Daedalus.Application.DTOs;
using FluentValidation;

namespace Daedalus.Application.Validators;

public sealed class SendBrainstormMessageDtoValidator : AbstractValidator<SendBrainstormMessageDto>
{
    public SendBrainstormMessageDtoValidator()
    {
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Message content is required.")
            .MaximumLength(10000).WithMessage("Message cannot exceed 10,000 characters.");
    }
}
```

**Step 5: Build and run tests**

Run: `dotnet build && dotnet test tests/Daedalus.Tests.Unit.Application --filter "SendBrainstormMessageDtoValidatorTests"`
Expected: PASS (3 tests)

**Step 6: Commit**

```bash
git add src/Daedalus.Application/DTOs/BrainstormDtos.cs src/Daedalus.Application/Mappers/BrainstormDtoMapper.cs src/Daedalus.Application/Validators/SendBrainstormMessageDtoValidator.cs tests/Daedalus.Tests.Unit.Application/Validators/SendBrainstormMessageDtoValidatorTests.cs
git commit -m "feat: add brainstorm DTOs, mapper, and message validator"
```

---

## Task 9: BrainstormController

**Files:**
- Create: `src/Daedalus.Api/Controllers/BrainstormController.cs`
- Test: `tests/Daedalus.Tests.Unit/Controllers/BrainstormControllerTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Daedalus.Tests.Unit/Controllers/BrainstormControllerTests.cs
using Daedalus.Api.Controllers;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Controllers;

public class BrainstormControllerTests
{
    private readonly IBrainstormService _service = Substitute.For<IBrainstormService>();
    private readonly ILogger<BrainstormController> _logger = Substitute.For<ILogger<BrainstormController>>();
    private readonly BrainstormController _controller;

    public BrainstormControllerTests()
    {
        _controller = new BrainstormController(_service, _logger);
    }

    [Fact]
    public async Task CreateSession_WithValidProjectId_Returns200()
    {
        var projectId = Guid.NewGuid();
        var session = BrainstormSession.Create(projectId).Value;
        _service.CreateSessionAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        var result = await _controller.CreateSession(
            new Application.DTOs.CreateBrainstormSessionDto(projectId));

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateSession_WhenServiceFails_Returns400()
    {
        _service.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BrainstormSession>("Project not found"));

        var result = await _controller.CreateSession(
            new Application.DTOs.CreateBrainstormSessionDto(Guid.NewGuid()));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetSession_WithExistingId_Returns200()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _service.GetSessionAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        var result = await _controller.GetSession(session.Id);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SendMessage_WithValidContent_Returns200()
    {
        var sessionId = Guid.NewGuid();
        var message = BrainstormMessage.Create(MessageRole.Assistant, "Response", BrainstormPhase.Clarification).Value;
        _service.SendMessageAsync(sessionId, "Hello", Arg.Any<CancellationToken>())
            .Returns(Result.Success(message));

        var result = await _controller.SendMessage(
            sessionId,
            new Application.DTOs.SendBrainstormMessageDto("Hello"));

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AdvancePhase_WhenSignaled_Returns200()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _service.AdvancePhaseAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        var result = await _controller.AdvancePhase(session.Id);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AbandonSession_Returns204()
    {
        var sessionId = Guid.NewGuid();
        _service.AbandonSessionAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.AbandonSession(sessionId);

        result.Should().BeOfType<NoContentResult>();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit --filter "BrainstormControllerTests" --no-build`
Expected: FAIL — `BrainstormController` does not exist.

**Step 3: Write minimal implementation**

```csharp
// src/Daedalus.Api/Controllers/BrainstormController.cs
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Daedalus.Api.Controllers;

/// <summary>
///     API endpoints for interactive brainstorming sessions.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class BrainstormController(
    IBrainstormService brainstormService,
    ILogger<BrainstormController> logger) : ControllerBase
{
    /// <summary>
    ///     Creates a new brainstorming session and triggers context gathering.
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("sessions")]
    [ProducesResponseType(typeof(BrainstormSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSession(
        [FromBody] CreateBrainstormSessionDto request,
        CancellationToken ct = default)
    {
        var result = await brainstormService.CreateSessionAsync(request.ProjectId, ct);
        return result.IsSuccess
            ? Ok(BrainstormDtoMapper.ToDto(result.Value))
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Gets a brainstorming session with full conversation history.
    /// </summary>
    [Authorize(Policy = "CodeAnalysisRead")]
    [HttpGet("sessions/{sessionId:guid}")]
    [ProducesResponseType(typeof(BrainstormSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSession(Guid sessionId, CancellationToken ct = default)
    {
        var result = await brainstormService.GetSessionAsync(sessionId, ct);
        return result.IsSuccess
            ? Ok(BrainstormDtoMapper.ToDto(result.Value))
            : NotFound(new { error = result.Error });
    }

    /// <summary>
    ///     Lists brainstorming sessions for a project.
    /// </summary>
    [Authorize(Policy = "CodeAnalysisRead")]
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(List<BrainstormSessionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSessions(
        [FromQuery] Guid projectId,
        CancellationToken ct = default)
    {
        var result = await brainstormService.GetSessionsByProjectAsync(projectId, ct);
        return result.IsSuccess
            ? Ok(result.Value.Select(BrainstormDtoMapper.ToSummaryDto).ToList())
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Sends a user message and returns the LLM's response.
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("sessions/{sessionId:guid}/messages")]
    [ProducesResponseType(typeof(BrainstormMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendMessage(
        Guid sessionId,
        [FromBody] SendBrainstormMessageDto request,
        CancellationToken ct = default)
    {
        var result = await brainstormService.SendMessageAsync(sessionId, request.Content, ct);
        return result.IsSuccess
            ? Ok(BrainstormDtoMapper.ToDto(result.Value))
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Advances the session to the next phase (user confirms LLM's readiness signal).
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [EnableRateLimiting("llm-operations")]
    [HttpPost("sessions/{sessionId:guid}/advance")]
    [ProducesResponseType(typeof(BrainstormSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AdvancePhase(Guid sessionId, CancellationToken ct = default)
    {
        var result = await brainstormService.AdvancePhaseAsync(sessionId, ct);
        return result.IsSuccess
            ? Ok(BrainstormDtoMapper.ToDto(result.Value))
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Abandons the brainstorming session.
    /// </summary>
    [Authorize(Policy = "CodeAnalysis")]
    [HttpPost("sessions/{sessionId:guid}/abandon")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AbandonSession(Guid sessionId, CancellationToken ct = default)
    {
        var result = await brainstormService.AbandonSessionAsync(sessionId, ct);
        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    ///     Generates tasks from the brainstorming session's implementation plan.
    ///     Only callable when the session is in the TaskCreation phase.
    /// </summary>
    [Authorize(Policy = "TaskManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPost("sessions/{sessionId:guid}/generate-tasks")]
    [ProducesResponseType(typeof(List<Application.DTOs.TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateTasks(Guid sessionId, CancellationToken ct = default)
    {
        // This endpoint will be implemented in Task 10
        // when we add plan-to-tasks parsing
        return BadRequest(new { error = "Not yet implemented" });
    }
}
```

**Step 4: Build and run tests**

Run: `dotnet build && dotnet test tests/Daedalus.Tests.Unit --filter "BrainstormControllerTests"`
Expected: PASS (6 tests)

**Step 5: Commit**

```bash
git add src/Daedalus.Api/Controllers/BrainstormController.cs tests/Daedalus.Tests.Unit/Controllers/BrainstormControllerTests.cs
git commit -m "feat: add BrainstormController with 7 API endpoints"
```

---

## Task 10: DI Registration and Wiring

**Files:**
- Modify: `src/Daedalus.Api/Program.cs` — register `IBrainstormService` and `IBrainstormRepository`
- Modify: `src/Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs` — add repository registration

**Step 1: Register repository in InfrastructureServiceExtensions.cs**

In the `AddExternalServices` method, add after the existing repository registrations:

```csharp
// Register brainstorming session repository
services.AddScoped<IBrainstormRepository, BrainstormRepository>();
```

**Step 2: Register service in Program.cs**

Add after the existing query service registrations (around line 76):

```csharp
// Add brainstorming service
builder.Services.AddScoped<IBrainstormService, BrainstormService>();
```

Add the required using:

```csharp
using Daedalus.Application.Services.Brainstorm;
```

**Step 3: Verify build**

Run: `dotnet build`
Expected: 0 errors

**Step 4: Commit**

```bash
git add src/Daedalus.Api/Program.cs src/Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs
git commit -m "feat: register BrainstormService and BrainstormRepository in DI"
```

---

## Task 11: Blazor API Client Methods

**Files:**
- Modify: `src/Daedalus.Web/Services/ApiClient.cs` — add brainstorm API methods

**Step 1: Add brainstorm methods to ApiClient**

Add the following methods to the existing `ApiClient` class:

```csharp
// Brainstorm session methods
public async Task<Result<BrainstormSessionDto>> CreateBrainstormSessionAsync(
    CreateBrainstormSessionDto dto, CancellationToken ct = default) =>
    await PostAsync<BrainstormSessionDto>("/api/brainstorm/sessions", dto, ct);

public async Task<Result<BrainstormSessionDto>> GetBrainstormSessionAsync(
    Guid sessionId, CancellationToken ct = default) =>
    await GetAsync<BrainstormSessionDto>($"/api/brainstorm/sessions/{sessionId}", ct);

public async Task<Result<List<BrainstormSessionSummaryDto>>> GetBrainstormSessionsAsync(
    Guid projectId, CancellationToken ct = default) =>
    await GetAsync<List<BrainstormSessionSummaryDto>>($"/api/brainstorm/sessions?projectId={projectId}", ct);

public async Task<Result<BrainstormMessageDto>> SendBrainstormMessageAsync(
    Guid sessionId, SendBrainstormMessageDto dto, CancellationToken ct = default) =>
    await PostAsync<BrainstormMessageDto>($"/api/brainstorm/sessions/{sessionId}/messages", dto, ct);

public async Task<Result<BrainstormSessionDto>> AdvanceBrainstormPhaseAsync(
    Guid sessionId, CancellationToken ct = default) =>
    await PostAsync<BrainstormSessionDto>($"/api/brainstorm/sessions/{sessionId}/advance", new { }, ct);

public async Task<Result> AbandonBrainstormSessionAsync(
    Guid sessionId, CancellationToken ct = default) =>
    await PostAsync<object>($"/api/brainstorm/sessions/{sessionId}/abandon", new { }, ct)
        .Map(_ => Result.Success());

public async Task<Result<List<TaskDto>>> GenerateBrainstormTasksAsync(
    Guid sessionId, CancellationToken ct = default) =>
    await PostAsync<List<TaskDto>>($"/api/brainstorm/sessions/{sessionId}/generate-tasks", new { }, ct);
```

**Step 2: Verify build**

Run: `dotnet build src/Daedalus.Web`
Expected: 0 errors

**Step 3: Commit**

```bash
git add src/Daedalus.Web/Services/ApiClient.cs
git commit -m "feat: add brainstorm session API methods to Blazor ApiClient"
```

---

## Task 12: Blazor Brainstorm Chat Page

**Files:**
- Create: `src/Daedalus.Web/Pages/Brainstorm.razor`

**Step 1: Create the Brainstorm page component**

```razor
@* src/Daedalus.Web/Pages/Brainstorm.razor *@
@page "/brainstorm/{SessionId:guid}"
@inject ApiClient Api
@inject NavigationManager Navigation
@inject NotificationService NotificationService
@implements IAsyncDisposable

<RadzenStack Gap="0" Style="height: calc(100vh - 80px); display: flex; flex-direction: column;">

    @* Phase indicator bar *@
    <RadzenStack Orientation="Orientation.Horizontal" AlignItems="AlignItems.Center"
                 Gap="0.25rem" class="rz-p-2 rz-border-bottom" Style="flex-shrink: 0;">
        @foreach (var phase in _phases)
        {
            var isCurrent = _session?.Phase == phase.Value;
            var isCompleted = _session is not null && (int)_session.Phase > (int)phase.Value;
            var style = isCurrent ? "font-weight: bold; color: var(--rz-primary);"
                : isCompleted ? "color: var(--rz-success);"
                : "opacity: 0.4;";
            <RadzenBadge Text="@phase.Label" IsPill="true"
                         BadgeStyle="@(isCurrent ? BadgeStyle.Primary : isCompleted ? BadgeStyle.Success : BadgeStyle.Light)"
                         Style="@style"/>
        }
    </RadzenStack>

    @* Message history *@
    <RadzenStack Gap="0.5rem" class="rz-p-4" Style="flex: 1; overflow-y: auto;" @ref="_messageContainer">
        @if (_session is not null)
        {
            @foreach (var message in _session.Messages)
            {
                var isUser = message.Role == "User";
                <RadzenCard Style="@(isUser
                    ? "margin-left: 20%; background: var(--rz-primary-lighter);"
                    : "margin-right: 20%;")">
                    <RadzenText TextStyle="TextStyle.Caption" Style="opacity: 0.6; margin-bottom: 0.25rem;">
                        @(isUser ? "You" : "Assistant")
                    </RadzenText>
                    <RadzenText TextStyle="TextStyle.Body2" Style="white-space: pre-wrap;">
                        @message.Content
                    </RadzenText>
                </RadzenCard>
            }
        }

        @if (_loading)
        {
            <RadzenCard Style="margin-right: 20%; opacity: 0.6;">
                <RadzenText TextStyle="TextStyle.Body2">Thinking...</RadzenText>
            </RadzenCard>
        }
    </RadzenStack>

    @* Advance button *@
    @if (_session?.PhaseCompleteSignaled == true)
    {
        <RadzenStack Orientation="Orientation.Horizontal" JustifyContent="JustifyContent.Center"
                     class="rz-p-2 rz-border-top" Style="flex-shrink: 0;">
            <RadzenButton Text="@($"Advance to {GetNextPhaseName()}")" Icon="arrow_forward"
                          ButtonStyle="ButtonStyle.Success" Click="@AdvancePhase"
                          IsBusy="@_advancing" data-testid="btn-advance-phase"/>
        </RadzenStack>
    }

    @* Input area *@
    @if (_session?.Phase is not BrainstormPhase.Completed and not BrainstormPhase.Abandoned
         and not BrainstormPhase.TaskCreation)
    {
        <RadzenStack Orientation="Orientation.Horizontal" Gap="0.5rem"
                     class="rz-p-2 rz-border-top" Style="flex-shrink: 0;">
            <RadzenTextArea @bind-Value="@_userInput" Rows="2" Placeholder="Type your message..."
                            Style="flex: 1;" @onkeydown="@HandleKeyDown"
                            data-testid="brainstorm-input"/>
            <RadzenButton Text="Send" Icon="send" ButtonStyle="ButtonStyle.Primary"
                          Click="@SendMessage" IsBusy="@_loading"
                          Disabled="@string.IsNullOrWhiteSpace(_userInput)"
                          data-testid="btn-send-message"/>
        </RadzenStack>
    }

    @* Task creation phase *@
    @if (_session?.Phase == BrainstormPhase.TaskCreation)
    {
        <RadzenStack class="rz-p-4" Style="flex-shrink: 0;">
            <RadzenButton Text="Generate Tasks from Plan" Icon="playlist_add"
                          ButtonStyle="ButtonStyle.Primary" Click="@GenerateTasks"
                          IsBusy="@_generatingTasks" data-testid="btn-generate-tasks"/>
        </RadzenStack>
    }
</RadzenStack>

@code {
    [Parameter] public Guid SessionId { get; set; }

    private BrainstormSessionDto? _session;
    private string _userInput = "";
    private bool _loading;
    private bool _advancing;
    private bool _generatingTasks;
    private RadzenStack? _messageContainer;
    private CancellationTokenSource? _cts;

    private static readonly (string Label, BrainstormPhase Value)[] _phases =
    [
        ("Context", BrainstormPhase.ContextGathering),
        ("Clarify", BrainstormPhase.Clarification),
        ("Propose", BrainstormPhase.Proposals),
        ("Design", BrainstormPhase.DesignReview),
        ("Plan", BrainstormPhase.PlanGeneration),
        ("Tasks", BrainstormPhase.TaskCreation)
    ];

    protected override async Task OnInitializedAsync()
    {
        _cts = new CancellationTokenSource();
        await LoadSession();
    }

    private async Task LoadSession()
    {
        var result = await Api.GetBrainstormSessionAsync(SessionId, _cts?.Token ?? CancellationToken.None);
        result.Match(
            session => _session = session,
            error => NotificationService.Notify(NotificationSeverity.Error, "Error", error));
    }

    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_userInput)) return;

        var message = _userInput;
        _userInput = "";
        _loading = true;

        try
        {
            var result = await Api.SendBrainstormMessageAsync(
                SessionId, new SendBrainstormMessageDto(message), _cts?.Token ?? CancellationToken.None);

            result.Match(
                _ => { },
                error => NotificationService.Notify(NotificationSeverity.Error, "Error", error));

            await LoadSession();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task AdvancePhase()
    {
        _advancing = true;
        try
        {
            var result = await Api.AdvanceBrainstormPhaseAsync(
                SessionId, _cts?.Token ?? CancellationToken.None);

            result.Match(
                session => _session = session,
                error => NotificationService.Notify(NotificationSeverity.Error, "Error", error));
        }
        finally
        {
            _advancing = false;
        }
    }

    private async Task GenerateTasks()
    {
        _generatingTasks = true;
        try
        {
            var result = await Api.GenerateBrainstormTasksAsync(
                SessionId, _cts?.Token ?? CancellationToken.None);

            result.Match(
                tasks =>
                {
                    NotificationService.Notify(NotificationSeverity.Success,
                        "Tasks Created", $"{tasks.Count} tasks generated");
                    Navigation.NavigateTo("/tasks");
                },
                error => NotificationService.Notify(NotificationSeverity.Error, "Error", error));
        }
        finally
        {
            _generatingTasks = false;
        }
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e is { Key: "Enter", ShiftKey: false })
        {
            await SendMessage();
        }
    }

    private string GetNextPhaseName() => _session?.Phase switch
    {
        BrainstormPhase.ContextGathering => "Clarification",
        BrainstormPhase.Clarification => "Proposals",
        BrainstormPhase.Proposals => "Design Review",
        BrainstormPhase.DesignReview => "Plan Generation",
        BrainstormPhase.PlanGeneration => "Task Creation",
        BrainstormPhase.TaskCreation => "Complete",
        _ => "Next Phase"
    };

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Daedalus.Web`
Expected: 0 errors

**Step 3: Commit**

```bash
git add src/Daedalus.Web/Pages/Brainstorm.razor
git commit -m "feat: add Blazor brainstorm chat page with phase indicator"
```

---

## Task 13: Generate Tasks Endpoint Implementation

**Files:**
- Modify: `src/Daedalus.Application/Abstractions/IBrainstormService.cs` — add `GenerateTasksAsync`
- Modify: `src/Daedalus.Application/Services/Brainstorm/BrainstormService.cs` — implement plan-to-tasks parsing
- Modify: `src/Daedalus.Api/Controllers/BrainstormController.cs` — wire up generate-tasks endpoint
- Test: `tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormServiceGenerateTasksTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormServiceGenerateTasksTests.cs
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services.Brainstorm;

public class BrainstormServiceGenerateTasksTests
{
    private readonly IBrainstormRepository _repository = Substitute.For<IBrainstormRepository>();
    private readonly IRalphAgentFactory _agentFactory = Substitute.For<IRalphAgentFactory>();
    private readonly IProjectRepository _projectRepository = Substitute.For<IProjectRepository>();
    private readonly IPrdService _prdService = Substitute.For<IPrdService>();
    private readonly ILogger<BrainstormService> _logger = Substitute.For<ILogger<BrainstormService>>();

    [Fact]
    public async Task GenerateTasksAsync_InTaskCreationPhase_ShouldCallPrdService()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;

        // Advance to TaskCreation phase
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        session.SetImplementationPlan("### Task 1: Create entity\nSome plan content");
        session.Phase.Should().Be(BrainstormPhase.TaskCreation);

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult(
                """[{"title":"Create entity","description":"Build the domain entity","acceptanceCriteria":"Entity exists","priority":"Medium","estimatedComplexity":"Medium","dependencies":[],"phase":"Backend","parallelGroup":1}]""",
                100, 200, "claude")));

        var expectedTasks = new List<TaskDto>();
        _prdService.ConvertToTasksAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedTasks));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var service = new BrainstormService(_repository, _agentFactory, _projectRepository, _prdService, _logger);

        // Act
        var result = await service.GenerateTasksAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateTasksAsync_NotInTaskCreationPhase_ShouldFail()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        // Still in ContextGathering phase

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        var service = new BrainstormService(_repository, _agentFactory, _projectRepository, _prdService, _logger);

        // Act
        var result = await service.GenerateTasksAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("TaskCreation");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daedalus.Tests.Unit.Application --filter "BrainstormServiceGenerateTasksTests" --no-build`
Expected: FAIL — `GenerateTasksAsync` method does not exist.

**Step 3: Implement generate tasks**

Add to `IBrainstormService`:

```csharp
Task<Result<List<TaskDto>>> GenerateTasksAsync(Guid sessionId, CancellationToken ct);
```

Add `IPrdService` dependency to `BrainstormService` constructor and implement:

```csharp
public async Task<Result<List<TaskDto>>> GenerateTasksAsync(Guid sessionId, CancellationToken ct)
{
    var sessionResult = await repository.GetByIdAsync(sessionId, ct);
    if (sessionResult.IsFailure)
        return Result.Failure<List<TaskDto>>(sessionResult.Error);

    var session = sessionResult.Value;

    if (session.Phase != BrainstormPhase.TaskCreation)
        return Result.Failure<List<TaskDto>>("Session must be in TaskCreation phase to generate tasks.");

    if (string.IsNullOrWhiteSpace(session.ImplementationPlan))
        return Result.Failure<List<TaskDto>>("No implementation plan available.");

    // Use LLM to parse the plan into structured PrdItemForConversionDto JSON
    var parsePrompt = $"""
        Parse the following implementation plan into a JSON array of task objects.
        Each object must have: title, description, acceptanceCriteria, priority (High/Medium/Low),
        estimatedComplexity (High/Medium/Low), dependencies (array of TASK-NNN strings),
        phase (string), parallelGroup (integer).

        Return ONLY valid JSON array, no markdown, no code blocks.

        Implementation Plan:
        {session.ImplementationPlan}
        """;

    var llmResult = await agentFactory.InvokeAsync(parsePrompt, ct);
    if (llmResult.IsFailure)
        return Result.Failure<List<TaskDto>>($"Failed to parse plan: {llmResult.Error}");

    // Parse the JSON response into PrdItemForConversionDto list
    try
    {
        var items = System.Text.Json.JsonSerializer.Deserialize<List<PrdItemForConversionDto>>(
            llmResult.Value.Response,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];

        // Reuse existing convert-to-tasks flow
        var result = await prdService.ConvertToTasksAsync(session.ProjectId, items, ct);

        if (result.IsSuccess)
        {
            session.AddMessage(MessageRole.System, $"Generated {result.Value.Count} tasks from implementation plan.");
            session.AddMessage(MessageRole.Assistant, "Tasks created. [PHASE_COMPLETE]");
            session.AdvancePhase(); // Move to Completed
            await repository.UpdateAsync(session, ct);
        }

        return result;
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Result.Failure<List<TaskDto>>($"Failed to parse LLM response as task list: {ex.Message}");
    }
}
```

Update the controller's `GenerateTasks` method to call the service:

```csharp
public async Task<IActionResult> GenerateTasks(Guid sessionId, CancellationToken ct = default)
{
    var result = await brainstormService.GenerateTasksAsync(sessionId, ct);
    return result.IsSuccess
        ? Ok(result.Value)
        : BadRequest(new { error = result.Error });
}
```

**Step 4: Build and run tests**

Run: `dotnet build && dotnet test tests/Daedalus.Tests.Unit.Application --filter "BrainstormServiceGenerateTasksTests"`
Expected: PASS (2 tests)

**Step 5: Commit**

```bash
git add src/Daedalus.Application/Abstractions/IBrainstormService.cs src/Daedalus.Application/Services/Brainstorm/BrainstormService.cs src/Daedalus.Api/Controllers/BrainstormController.cs tests/Daedalus.Tests.Unit.Application/Services/Brainstorm/BrainstormServiceGenerateTasksTests.cs
git commit -m "feat: implement generate-tasks endpoint with plan-to-task parsing"
```

---

## Task 14: Full Build Verification and Integration Test

**Files:**
- No new files — verify everything compiles and unit tests pass

**Step 1: Full build**

Run: `dotnet build`
Expected: 0 errors

**Step 2: Run all unit tests**

Run: `dotnet test tests/Daedalus.Tests.Unit tests/Daedalus.Tests.Unit.Application tests/Daedalus.Tests.Unit.Domain tests/Daedalus.Tests.Unit.Infrastructure --verbosity minimal`
Expected: All tests PASS (existing + new brainstorm tests)

**Step 3: Verify Scalar API reference includes new endpoints**

Run the API in development mode and check `/scalar/v1` — the 7 new brainstorm endpoints should appear.

**Step 4: Commit (if any fixes were needed)**

```bash
git commit -m "fix: address build and test issues from brainstorm integration"
```
