from decimal import Decimal

import pytest

from app.contracts.validation import ContractValidator


@pytest.mark.parametrize('keyword,number,valid', [
    ('minimum', '0.1', True), ('minimum', '0.099999999999999999999', False),
    ('maximum', '0.3', True), ('maximum', '0.300000000000000000001', False),
    ('exclusiveMinimum', '0.1', False), ('exclusiveMinimum', '0.100000000000000000001', True),
    ('exclusiveMaximum', '0.3', False), ('exclusiveMaximum', '0.299999999999999999999', True),
])
def test_decimal_schema_limits_use_exact_decimal_comparisons(keyword, number, valid):
    bound = 0.1 if keyword in ('minimum', 'exclusiveMinimum') else 0.3
    validator = ContractValidator({'type': 'number', keyword: bound})
    assert validator.is_valid(Decimal(number)) == valid


@pytest.mark.parametrize('keyword,number', [('minimum', 0.1), ('maximum', 0.3)])
def test_internal_float_inputs_and_http_decimal_inputs_agree_at_inclusive_limits(keyword, number):
    validator = ContractValidator({'type': 'number', keyword: number})
    assert validator.is_valid(number)
    assert validator.is_valid(Decimal(str(number)))
