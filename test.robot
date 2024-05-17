*** Settings ***
Suite Setup                   Setup
Suite Teardown                Teardown
Test Timeout                  30 seconds
Test Setup                    Reset Emulation
Test Teardown                 Test Teardown
Resource                      ${RENODEKEYWORDS}

*** Variables ***
${SCRIPT}                     ${CURDIR}/test.resc
${UART}                       sysbus.usart6


*** Keywords ***
Load Script
    Execute Script            ${SCRIPT}
    Create Terminal Tester    ${UART}


*** Test Cases ***
Should Run Test Case
    Load Script
    Start Emulation
    Wait For Line On Uart     INFO:<Platform I2C Test>
    Wait For Line On Uart     PASS:<Multi-byte read 0x2D>
    Wait For Line On Uart     EXIT:<done>

    [Teardown]                Test Teardown
