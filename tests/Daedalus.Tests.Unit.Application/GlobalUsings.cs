global using CSharpFunctionalExtensions;
global using Daedalus.Tests.Unit.Application.Abstractions;
global using AwesomeAssertions;
global using NSubstitute;
global using NSubstitute.ExceptionExtensions;
global using Xunit;
global using DomainTask = Daedalus.Domain.Entities.Task;

// Note: Daedalus.Domain.Entities is NOT imported globally to avoid ambiguity with System.Threading.Tasks.Task
