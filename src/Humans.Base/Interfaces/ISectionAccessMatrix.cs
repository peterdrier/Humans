using Humans.Base.Models;

namespace Humans.Base.Interfaces;

/// <summary>
/// The help-widget access matrices a section owns — the "who can do what" table behind
/// <c>&lt;vc:access-matrix section="..."&gt;</c> and the agent's preloaded access-matrix corpus.
/// A section declares the rows for the pages it owns, and the two consumers inject
/// <c>IEnumerable&lt;ISectionAccessMatrix&gt;</c> and render what DI discovered, so neither Base
/// nor Shell holds a list of which sections have a matrix. One section may contribute several
/// entries (CityPlanning owns the overview and the barrio map); the flat rendered order comes
/// from <see cref="AccessMatrixData.Order"/>, not from registration order.
/// </summary>
public interface ISectionAccessMatrix : ISectionContribution
{
    IReadOnlyList<AccessMatrixData> AccessMatrices { get; }
}
