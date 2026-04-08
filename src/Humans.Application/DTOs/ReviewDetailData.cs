using Humans.Domain.Entities;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.DTOs;

public record ReviewDetailData(
    Profile? Profile,
    int ConsentCount,
    int RequiredConsentCount,
    MemberApplication? PendingApplication);
