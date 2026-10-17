using Humans.Base.Interfaces;
using Humans.Campaigns.Contracts;

namespace Humans.Campaigns.Services;

/// <summary>
/// Full Campaigns service surface. Other sections depend on <see cref="ICampaignService"/> for
/// reads and <see cref="ICampaignWrite"/> for the two writes they make.
/// </summary>
internal interface ICampaignManagement : ICampaignService, ICampaignWrite, IApplicationService;
