import math

import pytest

from dreadnought.stats import (
    ewma,
    mann_whitney_greater,
    normal_sf,
    robust_z_scores,
    two_proportion_p_value,
)


def test_normal_sf_known_points():
    assert normal_sf(0) == pytest.approx(0.5)
    assert normal_sf(1.6449) == pytest.approx(0.05, abs=1e-4)
    assert normal_sf(-1.6449) == pytest.approx(0.95, abs=1e-4)


def test_robust_z_is_not_dragged_by_the_outlier_it_measures():
    values = [10, 11, 9, 10, 12, 10, 11, 9, 10, 500]
    scores = robust_z_scores(values)

    assert scores[-1] > 100  # a mean/stddev z-score would cap near 3 here
    assert all(abs(s) < 3.5 for s in scores[:-1])


def test_robust_z_falls_back_when_most_values_are_identical():
    scores = robust_z_scores([5, 5, 5, 5, 5, 5, 5, 50])

    assert scores[:7] == [0.0] * 7
    assert scores[7] > 3.5


def test_robust_z_degenerate_inputs():
    assert robust_z_scores([]) == []
    assert robust_z_scores([3, 3, 3]) == [0.0, 0.0, 0.0]


def test_two_proportion_matches_hand_calculation():
    # 9/50 vs 1/50: pooled p = 0.1, se = sqrt(0.1*0.9*(2/50)) = 0.06, z = 0.16/0.06
    expected = normal_sf(0.16 / 0.06)

    assert two_proportion_p_value(9, 50, 1, 50) == pytest.approx(expected)


def test_two_proportion_is_one_sided():
    worse = two_proportion_p_value(9, 50, 1, 50)
    better = two_proportion_p_value(1, 50, 9, 50)

    assert worse < 0.01 < 0.99 < better
    assert worse + better == pytest.approx(1.0)


def test_two_proportion_with_no_variation_gives_no_evidence():
    assert two_proportion_p_value(0, 30, 0, 30) == 1.0
    assert two_proportion_p_value(30, 30, 30, 30) == 1.0


@pytest.mark.parametrize("args", [(1, 0, 0, 10), (11, 10, 0, 10), (-1, 10, 0, 10)])
def test_two_proportion_rejects_impossible_counts(args):
    with pytest.raises(ValueError):
        two_proportion_p_value(*args)


def test_mann_whitney_detects_a_clear_shift():
    control = [20 + (i % 7) for i in range(40)]
    slower = [v + 6 for v in control]

    result = mann_whitney_greater(slower, control)

    assert result.p_value < 1e-6
    assert result.superiority > 0.9


def test_mann_whitney_identical_groups_show_no_effect():
    values = [float(i % 9) for i in range(40)]

    result = mann_whitney_greater(values, values)

    assert result.superiority == pytest.approx(0.5)
    assert result.p_value > 0.4


def test_mann_whitney_matches_exact_u_on_a_small_case():
    # a = [3, 4, 5], b = [1, 2, 3]: 9 pairs, a>b in 8, one tie => U = 8.5
    result = mann_whitney_greater([3, 4, 5], [1, 2, 3])

    assert result.superiority == pytest.approx(8.5 / 9)


def test_mann_whitney_all_tied_is_safe():
    result = mann_whitney_greater([1.0] * 10, [1.0] * 10)

    assert (result.p_value, result.superiority) == (1.0, 0.5)


def test_ewma_smooths_and_validates():
    assert ewma([10, 20], alpha=0.5) == [10, 15]
    assert ewma([1, 2, 3], alpha=1) == [1, 2, 3]
    assert math.isclose(ewma([0, 10, 10, 10], 0.3)[-1], 6.57)
    with pytest.raises(ValueError):
        ewma([1], alpha=0)
