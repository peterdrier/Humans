---
name: NSubstitute — capture substitute factories to a local before passing into .Returns(...)
description: A method that creates and configures an NSubstitute mock must not be called inline as the argument to another .Returns(...) — NSubstitute can't tell which "last call" the outer Returns attaches to. Capture to a local first.
---

If a helper method internally creates a substitute and configures `.Returns(...)` on it, calling that helper inline as the argument of another `.Returns(...)` produces `CouldNotSetReturnDueToNoLastCallException` at runtime.

**Broken:**

```csharp
serviceProvider.GetService(typeof(IUserService))
    .Returns(NewDbBackedUserService());   // NewDbBackedUserService internally
                                          // calls userSvc.GetByIdAsync(...).Returns(...)
```

**Fixed:**

```csharp
var userService = NewDbBackedUserService();
serviceProvider.GetService(typeof(IUserService)).Returns(userService);
```

**Why:** NSubstitute tracks "the last call on a substitute" in thread-local state to know which call to attach a `Returns()` to. When `NewDbBackedUserService()` configures substitute calls during its execution, those become the last calls — by the time the outer `Returns(...)` is evaluated, the thread-local state points at the inner factory's substitute, not the outer `serviceProvider.GetService(...)` call. NSubstitute detects the mismatch and throws. Capturing the factory result into a local separates the two configuration scopes.

**How to apply:** Any time you write `.Returns(<expression that creates or mutates an NSubstitute mock>)`, lift the expression to a local. This applies to harness helpers like `NewDbBackedUserService()`, `UserInfoStubHelpers.StubGetUserInfosFromDb`, and any custom factory that wraps `Substitute.For<T>()` + `.Returns(...)` chaining.

**Related:** [[service-test-harness]] — the harness's `NewDbBackedUserService()` is the most common trigger.
